using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Indexing
{
    /// <summary>
    /// To minimize the number of RPCs, we process index updates for each grain on the silo where the grain is active. To do this processing, each silo
    /// has one or more <see cref="IndexWorkflowQueueGrainService"/>s for each grain class, up to the number of hardware threads. A GrainService is a grain that
    /// belongs to a specific silo.
    /// + Each of these GrainServices has a queue of workflowRecords, which describe updates that must be propagated to indexes. Each workflowRecord contains
    ///   the following information:
    ///    - workflowID: grainID + a sequence number
    ///    - memberUpdates: the updated values of indexed fields
    ///  
    ///   Ordinarily, these workflowRecords are for grains that are active on <see cref="IndexWorkflowQueueGrainService"/>'s silo. (This may not be true for
    ///   short periods when a grain migrates to another silo or after the silo recovers from failure).
    /// 
    /// + The <see cref="IndexWorkflowQueueGrainService"/> grain Q has a dictionary updatesOnWait is an in-memory dictionary that maps each grain G to the
    ///   workflowRecords for G that are waiting for be updated.
    /// </summary>
    internal class IndexWorkflowQueueBase : IIndexWorkflowQueue
    {
        //the persistent state of IndexWorkflowQueue, including:
        // - doubly linked list of workflowRecordds
        // - the identity of the IndexWorkflowQueue GrainService
        protected IndexWorkflowQueueState queueState;

        //the tail of workflowRecords doubly linked list
        internal IndexWorkflowRecordNode _workflowRecordsTail;

        //the grain storage for the index workflow queue
        private IGrainStorage __grainStorage;
        private IGrainStorage StorageProvider => __grainStorage ?? GetGrainStorage();

        private int _queueSeqNum;
        private Type _iGrainType;

        private bool HasAnyTotalIndex => GetHasAnyTotalIndex();
        private bool? __hasAnyTotalIndex = null;

        private bool _isDefinedAsFaultTolerantGrain;
        private bool IsFaultTolerant => _isDefinedAsFaultTolerantGrain && HasAnyTotalIndex;

        private IIndexWorkflowQueueHandler __handler;
        private IIndexWorkflowQueueHandler Handler => __handler ?? InitWorkflowQueueHandler();

        private int _isHandlerWorkerIdle;

        /// <summary>
        /// This lock is used to queue all the writes to the storage and do them in a single batch, i.e., group commit
        /// 
        /// Works hand-in-hand with pendingWriteRequests and writeRequestIdGen.
        /// </summary>
        private AsyncLock _writeLock;

        /// <summary>
        /// Creates a unique ID for each write request to the storage.
        /// 
        /// The values generated by this ID generator are used in pendingWriteRequests
        /// </summary>
        private int _writeRequestIdGen;

        /// <summary>
        /// All the write requests that are waiting behind write_lock are accumulated
        /// in this data structure, and all of them will be done at once.
        /// </summary>
        private HashSet<int> _pendingWriteRequests;

        public const int BATCH_SIZE = int.MaxValue;

        public static int NUM_AVAILABLE_INDEX_WORKFLOW_QUEUES => Environment.ProcessorCount;

        private SiloAddress _silo;
        private SiloIndexManager _siloIndexManager;
        private Lazy<GrainReference> _lazyParent;

        internal IndexWorkflowQueueBase(SiloIndexManager siloIndexManager, Type grainInterfaceType, int queueSequenceNumber, SiloAddress silo,
                                        bool isDefinedAsFaultTolerantGrain, Func<GrainReference> parentFunc)
        {
            queueState = new IndexWorkflowQueueState(silo);
            _iGrainType = grainInterfaceType;
            _queueSeqNum = queueSequenceNumber;

            _workflowRecordsTail = null;
            __grainStorage = null;
            __handler = null;
            _isHandlerWorkerIdle = 1;

            _isDefinedAsFaultTolerantGrain = isDefinedAsFaultTolerantGrain;

            _writeLock = new AsyncLock();
            _writeRequestIdGen = 0;
            _pendingWriteRequests = new HashSet<int>();

            _silo = silo;
            _siloIndexManager = siloIndexManager;
            _lazyParent = new Lazy<GrainReference>(parentFunc, true);
        }

        private IIndexWorkflowQueueHandler InitWorkflowQueueHandler() 
            => __handler = _lazyParent.Value.IsGrainService
                ? _siloIndexManager.GetGrainService<IIndexWorkflowQueueHandler>(
                        IndexWorkflowQueueHandlerBase.CreateIndexWorkflowQueueHandlerGrainReference(_siloIndexManager, _iGrainType, _queueSeqNum, _silo))
                : _siloIndexManager.GrainFactory.GetGrain<IIndexWorkflowQueueHandler>(CreateIndexWorkflowQueuePrimaryKey(_iGrainType, _queueSeqNum));

        public Task AddAllToQueue(Immutable<List<IndexWorkflowRecord>> workflowRecords)
        {
            List<IndexWorkflowRecord> newWorkflows = workflowRecords.Value;
            foreach (IndexWorkflowRecord newWorkflow in newWorkflows)
            {
                AddToQueueNonPersistent(newWorkflow);
            }

            InitiateWorkerThread();
            return IsFaultTolerant ? PersistState() : Task.CompletedTask;
        }

        public Task AddToQueue(Immutable<IndexWorkflowRecord> workflow)
        {
            AddToQueueNonPersistent(workflow.Value);

            InitiateWorkerThread();
            return IsFaultTolerant ? PersistState() : Task.CompletedTask;
        }

        private void AddToQueueNonPersistent(IndexWorkflowRecord newWorkflow)
        {
            var newWorkflowNode = new IndexWorkflowRecordNode(newWorkflow);
            if (_workflowRecordsTail == null) //if the list is empty
            {
                _workflowRecordsTail = newWorkflowNode;
                queueState.State.WorkflowRecordsHead = newWorkflowNode;
            }
            else // otherwise append to the end of the list
            {
                _workflowRecordsTail.Append(newWorkflowNode, ref _workflowRecordsTail);
            }
        }

        public Task RemoveAllFromQueue(Immutable<List<IndexWorkflowRecord>> workflowRecords)
        {
            List<IndexWorkflowRecord> newWorkflows = workflowRecords.Value;
            foreach (IndexWorkflowRecord newWorkflow in newWorkflows)
            {
                RemoveFromQueueNonPersistent(newWorkflow);
            }
            return IsFaultTolerant ? PersistState() : Task.CompletedTask;
        }

        private void RemoveFromQueueNonPersistent(IndexWorkflowRecord newWorkflow)
        {
            for (var current = queueState.State.WorkflowRecordsHead; current != null; current = current.Next)
            {
                if (newWorkflow.Equals(current.WorkflowRecord))
                {
                    current.Remove(ref queueState.State.WorkflowRecordsHead, ref _workflowRecordsTail);
                    return;
                }
            }
        }

        private void InitiateWorkerThread()
        {
            if (Interlocked.Exchange(ref _isHandlerWorkerIdle, 0) == 1)
            {
                IndexWorkflowRecordNode punctuatedHead = AddPunctuationAt(BATCH_SIZE);
                Handler.HandleWorkflowsUntilPunctuation(punctuatedHead.AsImmutable()).Ignore();
            }
        }

        private IndexWorkflowRecordNode AddPunctuationAt(int batchSize)
        {
            if (_workflowRecordsTail == null) throw new WorkflowIndexException("Adding a punctuation to an empty work-flow queue is not possible.");

            var punctuationHead = queueState.State.WorkflowRecordsHead;
            if (punctuationHead.IsPunctuation()) throw new WorkflowIndexException("The element at the head of work-flow queue cannot be a punctuation.");

            if (batchSize == int.MaxValue)
            {
                var punctuation = _workflowRecordsTail.AppendPunctuation(ref _workflowRecordsTail);
                return punctuationHead;
            }
            var punctuationLoc = punctuationHead;

            for (int i = 1; i < batchSize && punctuationLoc.Next != null; ++i)
            {
                punctuationLoc = punctuationLoc.Next;
            }
            punctuationLoc.AppendPunctuation(ref _workflowRecordsTail);
            return punctuationHead;
        }

        private List<IndexWorkflowRecord> RemoveFromQueueUntilPunctuation(IndexWorkflowRecordNode from)
        {
            List<IndexWorkflowRecord> workflowRecords = new List<IndexWorkflowRecord>();
            if (from != null && !from.IsPunctuation())
            {
                workflowRecords.Add(from.WorkflowRecord);
            }

            IndexWorkflowRecordNode tmp = from?.Next;
            while (tmp != null && !tmp.IsPunctuation())
            {
                workflowRecords.Add(tmp.WorkflowRecord);
                tmp = tmp.Next;
                tmp.Prev.Clean();
            }

            if (tmp == null) from.Remove(ref queueState.State.WorkflowRecordsHead, ref _workflowRecordsTail);
            else
            {
                from.Next = tmp;
                tmp.Prev = from;
                from.Remove(ref queueState.State.WorkflowRecordsHead, ref _workflowRecordsTail);
                tmp.Remove(ref queueState.State.WorkflowRecordsHead, ref _workflowRecordsTail);
            }

            return workflowRecords;
        }

        private async Task PersistState()
        {
            //create a write-request ID, which is used for group commit
            int writeRequestId = ++_writeRequestIdGen;

            //add the write-request ID to the pending write requests
            _pendingWriteRequests.Add(writeRequestId);

            //wait before any previous write is done
            using (await _writeLock.LockAsync())
            {
                // If the write request is not there, it was handled by another worker before we obtained the lock.
                if (_pendingWriteRequests.Contains(writeRequestId))
                {
                    //clear all pending write requests, as this attempt will do them all.
                    _pendingWriteRequests.Clear();

                    //write the state back to the storage
                    string grainType = "Orleans.Indexing.IndexWorkflowQueue-" + IndexUtils.GetFullTypeName(_iGrainType);
                    var saveETag = this.queueState.ETag;
                    try
                    {
                        this.queueState.ETag = StorageProviderUtils.ANY_ETAG;
                        await StorageProvider.WriteStateAsync(grainType, _lazyParent.Value, this.queueState);
                    }
                    finally
                    {
                        if (this.queueState.ETag == StorageProviderUtils.ANY_ETAG)
                        {
                            this.queueState.ETag = saveETag;
                        }
                    }
                }
            }
        }

        public Task<Immutable<IndexWorkflowRecordNode>> GiveMoreWorkflowsOrSetAsIdle()
        {
            List<IndexWorkflowRecord> removedWorkflows = RemoveFromQueueUntilPunctuation(queueState.State.WorkflowRecordsHead);
            if (IsFaultTolerant)
            {
                //The task of removing the work-flow record IDs from the grain runs in parallel with persisting the state. At this point, there
                //is a possibility that some work-flow record IDs do not get removed from the indexable grains while the work-flow record is removed
                //from the queue. This is fine, because having some dangling work-flow IDs in some indexable grains is harmless.
                //TODO: add a garbage collector that runs once in a while and removes the dangling work-flow IDs (i.e., the work-flow IDs that exist in the
                //      indexable grain, but its corresponding work-flow record does not exist in the work-flow queue.
                //Task.WhenAll(
                //    RemoveWorkflowRecordsFromIndexableGrains(removedWorkflows),
                PersistState(//)
            ).Ignore();
            }

            if (_workflowRecordsTail == null)
            {
                _isHandlerWorkerIdle = 1;
                return Task.FromResult(new Immutable<IndexWorkflowRecordNode>(null));
            }
            else
            {
                _isHandlerWorkerIdle = 0;
                return Task.FromResult(AddPunctuationAt(BATCH_SIZE).AsImmutable());
            }
        }

        private bool GetHasAnyTotalIndex()
        {
            if (!__hasAnyTotalIndex.HasValue)
            {
                __hasAnyTotalIndex = _siloIndexManager.IndexFactory.GetGrainIndexes(_iGrainType).HasAnyTotalIndex;
            }
            return __hasAnyTotalIndex.Value;
        }

        private IGrainStorage GetGrainStorage()
            => __grainStorage = typeof(IndexWorkflowQueueGrainService).GetGrainStorage(_siloIndexManager.ServiceProvider);

        public Task<Immutable<List<IndexWorkflowRecord>>> GetRemainingWorkflowsIn(HashSet<Guid> activeWorkflowsSet)
        {
            var result = new List<IndexWorkflowRecord>();
            for (var current = queueState.State.WorkflowRecordsHead; current != null; current = current.Next)
            {
                if (activeWorkflowsSet.Contains(current.WorkflowRecord.WorkflowId))
                {
                    result.Add(current.WorkflowRecord);
                }
            }
            return Task.FromResult(result.AsImmutable());
        }

        public Task Initialize(IIndexWorkflowQueue oldParentGrainService)
            => throw new NotSupportedException();

        #region STATIC HELPER FUNCTIONS
        public static GrainReference CreateIndexWorkflowQueueGrainReference(SiloIndexManager siloIndexManager, Type grainInterfaceType, int queueSeqNum, SiloAddress siloAddress)
            => CreateGrainServiceGrainReference(siloIndexManager, grainInterfaceType, queueSeqNum, siloAddress);

        public static string CreateIndexWorkflowQueuePrimaryKey(Type grainInterfaceType, int queueSeqNum)
            => IndexUtils.GetFullTypeName(grainInterfaceType) + "-" + queueSeqNum;

        private static GrainReference CreateGrainServiceGrainReference(SiloIndexManager siloIndexManager, Type grainInterfaceType, int queueSeqNum, SiloAddress siloAddress)
            => siloIndexManager.MakeGrainServiceGrainReference(IndexingConstants.INDEX_WORKFLOW_QUEUE_GRAIN_SERVICE_TYPE_CODE,
                                                               CreateIndexWorkflowQueuePrimaryKey(grainInterfaceType, queueSeqNum), siloAddress);

        public static IIndexWorkflowQueue GetIndexWorkflowQueueFromGrainHashCode(SiloIndexManager siloIndexManager, Type grainInterfaceType, int grainHashCode, SiloAddress siloAddress)
        {
            int queueSeqNum = StorageProviderUtils.PositiveHash(grainHashCode, NUM_AVAILABLE_INDEX_WORKFLOW_QUEUES);
            var grainReference = CreateGrainServiceGrainReference(siloIndexManager, grainInterfaceType, queueSeqNum, siloAddress);
            return siloIndexManager.GetGrainService<IIndexWorkflowQueue>(grainReference);
        }
        #endregion STATIC HELPER FUNCTIONS
    }
}
