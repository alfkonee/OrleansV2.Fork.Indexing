using Orleans.CodeGeneration;
using Orleans.Indexing;
using System.Runtime.CompilerServices;

[assembly: GenerateSerializer(typeof(IndexableExtendedState<>))]
[assembly: GenerateSerializer(typeof(IndexWorkflowQueueState))]
[assembly: GenerateSerializer(typeof(IndexWorkflowQueueEntry))]
[assembly: GenerateSerializer(typeof(IndexWorkflowRecord))]
[assembly: GenerateSerializer(typeof(IndexWorkflowRecordNode))]
[assembly: GenerateSerializer(typeof(MemberUpdate))]
[assembly: GenerateSerializer(typeof(MemberUpdateOverridenOperation))]
[assembly: GenerateSerializer(typeof(MemberUpdateReverseTentative))]
[assembly: GenerateSerializer(typeof(MemberUpdateTentative))]
[assembly: GenerateSerializer(typeof(IndexMetaData))]
[assembly: GenerateSerializer(typeof(IndexUpdateGenerator))]
[assembly: GenerateSerializer(typeof(ActiveHashIndexPartitionedPerKey<,>))]
[assembly: GenerateSerializer(typeof(TotalHashIndexPartitionedPerKey<,>))]
[assembly: GenerateSerializer(typeof(HashIndexBucketState<,>))]
[assembly: GenerateSerializer(typeof(HashIndexSingleBucketEntry<>))]
[assembly: GenerateSerializer(typeof(OrleansFirstQueryResultStream<>))]
[assembly: GenerateSerializer(typeof(OrleansQueryResult<>))]
[assembly: GenerateSerializer(typeof(OrleansQueryResultStream<>))]
[assembly: GenerateSerializer(typeof(OrleansQueryResultStreamCaster<,>))]

[assembly: InternalsVisibleTo("Orleans.Indexing.Tests")]
