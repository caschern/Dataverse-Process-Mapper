using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DataverseProcessMapper.Models;
using Microsoft.Xrm.Sdk;

namespace DataverseProcessMapper.Data
{
    /// <summary>
    /// Keeps the definitions of one connection's processes in memory for the
    /// trigger column, definition search and previews, within a budget: past it
    /// the oldest definitions' text is dropped (their trigger summary stays) and
    /// is downloaded again when a search needs it.
    ///
    /// Thread-safe: the background trigger pass and a search can run at once.
    /// Their downloads take turns a batch at a time, so neither waits long and
    /// no definition is read twice. Owned by the control and recreated per
    /// connection, so nothing here outlives the environment it came from.
    ///
    /// <see cref="EnsureLoaded"/> and <see cref="Search"/> make requests (a
    /// batch's request runs while other calls wait their turn), so they belong
    /// on a background thread; <see cref="HeldChars"/> never waits and is safe
    /// to read from the UI thread.
    /// </summary>
    public sealed class DefinitionStore
    {
        // Same batch size as WorkflowRepository.LoadDefinitions' own requests.
        private const int BatchSize = 50;

        private readonly long _budgetChars;
        private readonly object _gate = new object();

        // Items whose text this store counts, oldest first, with the size counted.
        private readonly LinkedList<KeyValuePair<ProcessItem, long>> _held = new LinkedList<KeyValuePair<ProcessItem, long>>();
        private readonly Dictionary<ProcessItem, LinkedListNode<KeyValuePair<ProcessItem, long>>> _heldNodes =
            new Dictionary<ProcessItem, LinkedListNode<KeyValuePair<ProcessItem, long>>>();
        private long _heldChars; // changed under _gate, atomically: HeldChars reads it without the lock

        // Items whose trigger has been read. Their text may since have been dropped;
        // the trigger pass must not download them again for that.
        private readonly HashSet<ProcessItem> _summarised = new HashSet<ProcessItem>();

        /// <param name="budgetChars">Definition text kept, in characters (2 bytes each: 32M ≈ 64 MB).</param>
        public DefinitionStore(long budgetChars = 32_000_000)
        {
            _budgetChars = Math.Max(0, budgetChars);
        }

        public long BudgetChars => _budgetChars;

        /// <summary>Characters of definition text held right now.</summary>
        // Not under _gate, which a download holds for a whole request.
        public long HeldChars => Interlocked.Read(ref _heldChars);

        /// <summary>
        /// Forgets every process this store has seen, for a reload of the list on
        /// the same connection, so the old list's items are no longer referenced.
        /// The items keep their text and summaries. Cancel running passes first.
        /// </summary>
        public void Clear()
        {
            lock (_gate)
            {
                _held.Clear();
                _heldNodes.Clear();
                _summarised.Clear();
                Interlocked.Exchange(ref _heldChars, 0);
            }
        }

        /// <summary>
        /// Downloads the definitions of <paramref name="items"/> that are missing,
        /// a batch per request, and fills in each flow's
        /// <see cref="ProcessItem.TriggerSummary"/>. <paramref name="progress"/>
        /// receives (done, total) over all the items, first with what was already
        /// there and then after every batch. Cancellation is checked between
        /// batches and throws <see cref="OperationCanceledException"/>; what was
        /// read until then is kept.
        /// </summary>
        public void EnsureLoaded(IOrganizationService service, IList<ProcessItem> items,
            Action<int, int> progress, CancellationToken cancel)
        {
            var all = Distinct(items);
            List<ProcessItem> missing;
            lock (_gate)
            {
                foreach (var item in all.Where(i => i.DefinitionLoaded)) Absorb(item);
                missing = all.Where(i => !i.DefinitionLoaded && !_summarised.Contains(i)).ToList();
                EnforceBudget();
            }

            int done = all.Count - missing.Count;
            progress?.Invoke(done, all.Count);

            for (int start = 0; start < missing.Count; start += BatchSize)
            {
                cancel.ThrowIfCancellationRequested();
                var batch = missing.Skip(start).Take(BatchSize).ToList();
                lock (_gate)
                {
                    // Again: the wait for the lock lasts as long as the other caller's request.
                    cancel.ThrowIfCancellationRequested();
                    Download(service, batch);
                    EnforceBudget();
                }
                done += batch.Count;
                progress?.Invoke(done, all.Count);
            }
        }

        /// <summary>
        /// The processes among <paramref name="items"/> whose definition text
        /// (clientdata or xaml) contains <paramref name="term"/>, ignoring case, in
        /// the order given. Definitions not in memory are downloaded in batches;
        /// <paramref name="progress"/> receives (searched, total). Cancellation is
        /// checked between batches and throws <see cref="OperationCanceledException"/>.
        /// An empty or whitespace-only term matches nothing (it would match almost
        /// every definition, after downloading them all).
        /// </summary>
        public List<ProcessItem> Search(IOrganizationService service, IList<ProcessItem> items, string term,
            Action<int, int> progress, CancellationToken cancel)
        {
            var all = Distinct(items);
            var matches = new HashSet<ProcessItem>();
            if (string.IsNullOrWhiteSpace(term)) return new List<ProcessItem>();

            List<ProcessItem> missing;
            lock (_gate)
            {
                // Checked before the budget can drop anything.
                foreach (var item in all.Where(i => i.DefinitionLoaded))
                {
                    Absorb(item);
                    if (Contains(item, term)) matches.Add(item);
                }
                missing = all.Where(i => !i.DefinitionLoaded).ToList();
                EnforceBudget();
            }

            int done = all.Count - missing.Count;
            progress?.Invoke(done, all.Count);

            for (int start = 0; start < missing.Count; start += BatchSize)
            {
                cancel.ThrowIfCancellationRequested();
                var batch = missing.Skip(start).Take(BatchSize).ToList();
                lock (_gate)
                {
                    // Again: the wait for the lock lasts as long as the other caller's request.
                    cancel.ThrowIfCancellationRequested();
                    Download(service, batch);
                    foreach (var item in batch)
                        if (Contains(item, term)) matches.Add(item);
                    EnforceBudget();
                }
                done += batch.Count;
                progress?.Invoke(done, all.Count);
            }

            return all.Where(matches.Contains).ToList();
        }

        private static List<ProcessItem> Distinct(IList<ProcessItem> items)
            => (items ?? new ProcessItem[0]).Where(i => i != null).Distinct().ToList();

        private static bool Contains(ProcessItem item, string term)
            => (item.ClientData != null && item.ClientData.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
               || (item.Xaml != null && item.Xaml.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

        // ----- everything below runs under _gate -----

        /// <summary>Reads the batch's definitions (those another call has not read meanwhile) and summarises them.</summary>
        private void Download(IOrganizationService service, List<ProcessItem> batch)
        {
            var pending = batch.Where(i => !i.DefinitionLoaded).ToList();
            if (pending.Count > 0)
            {
                // One row per id: LoadDefinitions keys its batch by id, and two item
                // objects for one process (say, from two list reloads) share its definition.
                var byId = pending.GroupBy(i => i.Id).ToList();
                WorkflowRepository.LoadDefinitions(service, byId.Select(g => g.First()).ToList());
                foreach (var group in byId)
                {
                    var read = group.First();
                    foreach (var copy in group.Skip(1))
                    {
                        // Text before the flag, as LoadDefinitions does.
                        copy.Xaml = read.Xaml;
                        copy.ClientData = read.ClientData;
                        copy.DefinitionLoaded = read.DefinitionLoaded;
                    }
                }

                // A fresh download replaces a summary read from older text, in one
                // write so a reader never sees it blank. A flow that came back
                // without a definition (deleted, or no longer readable) keeps its own.
                foreach (var item in pending.Where(i => i.IsModernFlow && !string.IsNullOrWhiteSpace(i.ClientData)))
                    item.TriggerSummary = Parsing.TriggerSummary.ForFlow(item.ClientData);
            }
            foreach (var item in batch) Absorb(item);
        }

        /// <summary>Counts a loaded item's text and fills in its trigger summary when missing.</summary>
        private void Absorb(ProcessItem item)
        {
            if (item.TriggerSummary == null)
            {
                item.TriggerSummary = item.IsModernFlow
                    ? Parsing.TriggerSummary.ForFlow(item.ClientData)
                    : Parsing.TriggerSummary.ForClassic(item);
            }
            _summarised.Add(item);

            LinkedListNode<KeyValuePair<ProcessItem, long>> node;
            if (_heldNodes.TryGetValue(item, out node))
            {
                // Used again: the last to be dropped.
                _held.Remove(node);
                Interlocked.Add(ref _heldChars, -node.Value.Value);
                _heldNodes.Remove(item);
            }

            long size = (item.ClientData?.Length ?? 0) + (item.Xaml?.Length ?? 0);
            if (size == 0) return;
            _heldNodes[item] = _held.AddLast(new KeyValuePair<ProcessItem, long>(item, size));
            Interlocked.Add(ref _heldChars, size);
        }

        /// <summary>Drops the oldest definitions' text until the rest fits the budget.</summary>
        private void EnforceBudget()
        {
            while (_heldChars > _budgetChars && _held.First != null)
            {
                var oldest = _held.First.Value;
                _held.RemoveFirst();
                _heldNodes.Remove(oldest.Key);
                Interlocked.Add(ref _heldChars, -oldest.Value);

                // Not loaded first, so a reader that checks the flag reads it again.
                var item = oldest.Key;
                item.DefinitionLoaded = false;
                item.Xaml = null;
                item.ClientData = null;
            }
        }
    }
}
