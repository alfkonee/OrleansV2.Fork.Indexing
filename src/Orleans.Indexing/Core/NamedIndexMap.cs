﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Indexing
{
    internal class NamedIndexMap : IEnumerable<KeyValuePair<string, IndexInfo>>
    {
        private IDictionary<string, IndexInfo> IndexesByName { get; set; } = new Dictionary<string, IndexInfo>();

        internal IndexInfo this[string indexName]
        {
            get => this.IndexesByName[indexName];
            set => this.IndexesByName[indexName] = value;
        }

        internal bool TryGetValue(string indexName, out IndexInfo indexInfo)
            => this.IndexesByName.TryGetValue(indexName, out indexInfo);

        internal bool ContainsKey(string indexName) => this.IndexesByName.ContainsKey(indexName);

        internal int Count => this.IndexesByName.Count;

        internal bool Any(Func<IndexInfo, bool> pred) => this.IndexesByName.Values.Any(pred);

        internal bool HasAnyTotalIndex => this.Any(indexInfo => indexInfo.IndexInterface is ITotalIndex);
        internal bool HasAnyUniqueIndex => this.Any(indexInfo => indexInfo.MetaData.IsUniqueIndex);

        internal IEnumerable<string> Keys => this.IndexesByName.Keys;

        #region IEnumerable<KeyValuePair<string, IndexInfo>>

        IEnumerator IEnumerable.GetEnumerator() => this.IndexesByName.GetEnumerator();

        public IEnumerator<KeyValuePair<string, IndexInfo>> GetEnumerator() => this.IndexesByName.GetEnumerator();

        #endregion IEnumerable<KeyValuePair<string, IndexInfo>>
    }
}
