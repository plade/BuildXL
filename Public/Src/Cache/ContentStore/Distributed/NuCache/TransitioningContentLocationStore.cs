// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Redis;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Distributed;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Utils;
using BuildXL.Utilities.Collections;

namespace BuildXL.Cache.ContentStore.Distributed.NuCache
{
    /// <summary>
    /// <see cref="IContentLocationStore"/> implementation that supports old redis and new local location store.
    /// </summary>
    internal class TransitioningContentLocationStore : StartupShutdownBase, IContentLocationStore, IDistributedLocationStore
    {
        private readonly LocalLocationStore _localLocationStore;
        private readonly RedisContentLocationStore _redisContentLocationStore;
        private readonly RedisContentLocationStoreConfiguration _configuration;
        private readonly IClock _clock;

        /// <nodoc />
        public TransitioningContentLocationStore(
            RedisContentLocationStoreConfiguration configuration,
            RedisContentLocationStore redisContentLocationStore,
            LocalLocationStore localLocationStore,
            IClock clock)
        {
            Contract.Requires(configuration != null);
            Contract.Requires(clock != null);

            _configuration = configuration;
            _localLocationStore = localLocationStore;
            _redisContentLocationStore = redisContentLocationStore;
            _clock = clock;

            Contract.Assert(!_configuration.HasReadOrWriteMode(ContentLocationMode.Redis) || _redisContentLocationStore != null);
            Contract.Assert(!_configuration.HasReadOrWriteMode(ContentLocationMode.LocalLocationStore) || _localLocationStore != null);
        }

        /// <inheritdoc />
        public bool AreBlobsSupported =>
            (_configuration.HasReadOrWriteMode(ContentLocationMode.Redis) && _redisContentLocationStore.AreBlobsSupported) ||
            (_configuration.HasReadOrWriteMode(ContentLocationMode.LocalLocationStore) && _localLocationStore.AreBlobsSupported);

        /// <inheritdoc />
        public long MaxBlobSize => _configuration.MaxBlobSize;

        /// <nodoc />
        public int PageSize => _configuration.RedisBatchPageSize;

        /// <inheritdoc />
        protected override Tracer Tracer { get; } = new Tracer(nameof(TransitioningContentLocationStore));

        /// <inheritdoc />
        public MachineReputationTracker MachineReputationTracker
        {
            get
            {
                if (_configuration.HasReadMode(ContentLocationMode.Redis))
                {
                    return _redisContentLocationStore.MachineReputationTracker;
                }

                return _localLocationStore.MachineReputationTracker;
            }
        }

        /// <summary>
        /// Exposes <see cref="RedisContentLocationStore"/>. Mostly for testing purposes.
        /// </summary>
        public RedisContentLocationStore RedisContentLocationStore
        {
            get
            {
                Contract.Assert(_configuration.HasReadOrWriteMode(ContentLocationMode.Redis));
                return _redisContentLocationStore;
            }
        }

        /// <summary>
        /// Indicates if LocalLocationStore is enabled
        /// </summary>
        public bool IsLocalLocationStoreEnabled => _configuration.HasReadOrWriteMode(ContentLocationMode.LocalLocationStore);

        /// <summary>
        /// Exposes <see cref="LocalLocationStore"/>. Mostly for testing purposes.
        /// </summary>
        public LocalLocationStore LocalLocationStore
        {
            get
            {
                Contract.Assert(IsLocalLocationStoreEnabled);
                return _localLocationStore;
            }
        }

        /// <inheritdoc />
        protected override Task<BoolResult> StartupCoreAsync(OperationContext context)
        {
            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.StartupAsync(context.TracingContext),
                executeLocalLocationStore: () => _localLocationStore.StartupAsync(context.TracingContext));
        }

        /// <inheritdoc />
        protected override Task<BoolResult> ShutdownCoreAsync(OperationContext context)
        {
            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.ShutdownAsync(context.TracingContext),
                executeLocalLocationStore: () => _localLocationStore.ShutdownAsync(context.TracingContext));
        }

        /// <inheritdoc />
        public Task<GetBulkLocationsResult> GetBulkAsync(Context context, IReadOnlyList<ContentHash> contentHashes, CancellationToken cts, UrgencyHint urgencyHint, GetBulkOrigin origin)
        {
            var operationContext = new OperationContext(context, cts);
            if (!_configuration.HasReadMode(ContentLocationMode.Redis) || (origin == GetBulkOrigin.Local && _configuration.HasReadMode(ContentLocationMode.LocalLocationStore)))
            {
                return _localLocationStore.GetBulkAsync(operationContext, contentHashes, origin);
            }

            Contract.Assert(_redisContentLocationStore != null, "Read or Write mode should support ContentLocationMode.Redis.");
            return _redisContentLocationStore.GetBulkAsync(context, contentHashes, cts, urgencyHint, origin);
        }

        /// <inheritdoc />
        public Task<BoolResult> RegisterLocalLocationAsync(Context context, IReadOnlyList<ContentHashWithSize> contentHashes, CancellationToken cts, UrgencyHint urgencyHint, bool touch)
        {
            var operationContext = new OperationContext(context, cts);
            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.RegisterLocalLocationAsync(context, contentHashes, cts, urgencyHint, touch),
                executeLocalLocationStore: () => _localLocationStore.RegisterLocalLocationAsync(operationContext, contentHashes, touch));
        }

        /// <inheritdoc />
        public Task<BoolResult> TrimBulkAsync(Context context, IReadOnlyList<ContentHash> contentHashes, CancellationToken cts, UrgencyHint urgencyHint)
        {
            var operationContext = new OperationContext(context, cts);
            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.TrimBulkAsync(context, contentHashes, cts, urgencyHint),
                executeLocalLocationStore: () => _localLocationStore.TrimBulkAsync(operationContext, contentHashes));
        }

        /// <inheritdoc />
        public Task<BoolResult> TrimBulkAsync(Context context, IReadOnlyList<ContentHashAndLocations> contentHashToLocationMap, CancellationToken cts, UrgencyHint urgencyHint)
        {
            if (_redisContentLocationStore == null)
            {
                // If LLS is on, do nothing.
                // When LLS is on, the system's consistency is achieved by reconciliation.
                // When the content disappears from a machine it is machine's responsibility to "reconcile" and remove the locations from the system.
                // Other machines cannot "notify" the master and remove the locations for other machines.
                return BoolResult.SuccessTask;
            }

            return _redisContentLocationStore.TrimBulkAsync(context, contentHashToLocationMap, cts, urgencyHint);
        }

        /// <inheritdoc />
        public Task<BoolResult> TouchBulkAsync(Context context, IReadOnlyList<ContentHashWithSize> contentHashes, CancellationToken cts, UrgencyHint urgencyHint)
        {
            var operationContext = new OperationContext(context, cts);
            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.TouchBulkAsync(context, contentHashes, cts, urgencyHint),
                executeLocalLocationStore: () => _localLocationStore.TouchBulkAsync(operationContext, contentHashes.SelectList(c => c.Hash)));
        }

        private async Task<BoolResult> MultiExecuteAsync(Func<Task<BoolResult>> executeLocalLocationStore, Func<Task<BoolResult>> executeLegacyStore)
        {
            var legacyStoreResultTask = _configuration.HasWriteMode(ContentLocationMode.Redis) ? executeLegacyStore() : BoolResult.SuccessTask;
            var localStoreResultTask = _configuration.HasWriteMode(ContentLocationMode.LocalLocationStore) ? executeLocalLocationStore() : BoolResult.SuccessTask;

            // Wait for both tasks to avoid unobserved task error
            await Task.WhenAll(localStoreResultTask, legacyStoreResultTask);

            var legacyResult = await legacyStoreResultTask;
            var localStoreResult = await localStoreResultTask;

            return legacyResult & localStoreResult;
        }

        /// <inheritdoc />
        public CounterSet GetCounters(Context context)
        {
            var counters = new CounterSet();
            if (_configuration.HasReadOrWriteMode(ContentLocationMode.Redis))
            {
                counters.Merge(_redisContentLocationStore.GetCounters(context));
            }

            if (_configuration.HasReadOrWriteMode(ContentLocationMode.LocalLocationStore))
            {
                counters.Merge(_localLocationStore.GetCounters(context), "LLS.");
            }

            return counters;
        }

        /// <inheritdoc />
        public Task<ObjectResult<IList<ContentHashWithLastAccessTimeAndReplicaCount>>> TrimOrGetLastAccessTimeAsync(Context context, IList<Tuple<ContentHashWithLastAccessTimeAndReplicaCount, bool>> contentHashesWithInfo, CancellationToken cts, UrgencyHint urgencyHint)
        {
            Contract.Assert(_redisContentLocationStore != null, "Read or Write mode should support ContentLocationMode.Redis.");
            return _redisContentLocationStore.TrimOrGetLastAccessTimeAsync(context, contentHashesWithInfo, cts, urgencyHint);
        }

        /// <inheritdoc />
        public Task<BoolResult> UpdateBulkAsync(Context context, IReadOnlyList<ContentHashWithSizeAndLocations> contentHashesWithSizeAndLocations, CancellationToken cts, UrgencyHint urgencyHint, LocationStoreOption locationStoreOption)
        {
            Contract.Assert(_redisContentLocationStore != null, "Read or Write mode should support ContentLocationMode.Redis.");
            return _redisContentLocationStore.UpdateBulkAsync(context, contentHashesWithSizeAndLocations, cts, urgencyHint, locationStoreOption);
        }

        /// <inheritdoc />
        public Task<BoolResult> InvalidateLocalMachineAsync(Context context, ILocalContentStore localStore, CancellationToken cts)
        {
            var operationContext = new OperationContext(context, cts);
            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.InvalidateLocalMachineAsync(context, localStore, cts),
                executeLocalLocationStore: () => _localLocationStore.InvalidateLocalMachineAsync(operationContext));
        }

        /// <inheritdoc />
        public Task<BoolResult> GarbageCollectAsync(OperationContext context)
        {
            return _redisContentLocationStore.GarbageCollectAsync(context);
        }

        /// <inheritdoc />
        public void ReportReputation(MachineLocation location, MachineReputation reputation) =>
            MachineReputationTracker.ReportReputation(location, reputation);

        /// <inheritdoc />
        public Result<MachineLocation> GetRandomMachineLocation(IReadOnlyList<MachineLocation> except)
        {
            if (_configuration.HasReadMode(ContentLocationMode.Redis))
            {
                return _redisContentLocationStore.GetRandomMachineLocation(except);
            }

            return _localLocationStore.ClusterState.GetRandomMachineLocation(except);
        }

        /// <inheritdoc />
        public bool IsMachineActive(MachineLocation machine)
        {
            if (_configuration.HasReadMode(ContentLocationMode.Redis))
            {
                return _redisContentLocationStore.IsMachineActive(machine);
            }

            return _localLocationStore.ClusterState.TryResolveMachineId(machine, out var machineId)
                ? !_localLocationStore.ClusterState.InactiveMachines.Contains(machineId)
                : false;
        }

        #region IDistributedLocationStore Members

        /// <inheritdoc />
        public bool CanComputeLru => _configuration.HasReadMode(ContentLocationMode.LocalLocationStore);

        /// <inheritdoc />
        public Task<BoolResult> UnregisterAsync(Context context, IReadOnlyList<ContentHash> contentHashes, CancellationToken token)
        {
            return TrimBulkAsync(context, contentHashes, token, UrgencyHint.Nominal);
        }

        /// <inheritdoc />
        public IEnumerable<ContentHashWithLastAccessTimeAndReplicaCount> GetHashesInEvictionOrder(Context context, IReadOnlyList<ContentHashWithLastAccessTimeAndReplicaCount> contentHashesWithInfo)
        {
            Contract.Assert(
                _configuration.HasReadOrWriteMode(ContentLocationMode.LocalLocationStore),
                "GetLruPages can only be called when local location store is enabled");
            return _localLocationStore.GetHashesInEvictionOrder(context, contentHashesWithInfo, reverse: false);
        }

        /// <inheritdoc />
        public Task<BoolResult> PutBlobAsync(OperationContext context, ContentHash hash, byte[] blob)
        {
            Contract.Assert(AreBlobsSupported, "PutBlobAsync was called and blobs are not supported.");

            return MultiExecuteAsync(
                executeLegacyStore: () => _redisContentLocationStore.AreBlobsSupported ? _redisContentLocationStore.PutBlobAsync(context, hash, blob) : BoolResult.SuccessTask,
                executeLocalLocationStore: () => _localLocationStore.AreBlobsSupported ? _localLocationStore.PutBlobAsync(context, hash, blob) : BoolResult.SuccessTask);
        }

        /// <inheritdoc />
        public Task<Result<byte[]>> GetBlobAsync(OperationContext context, ContentHash hash)
        {
            Contract.Assert(AreBlobsSupported, "GetBlobAsync was called and blobs are not supported.");

            if (_configuration.HasReadMode(ContentLocationMode.LocalLocationStore))
            {
                if (_localLocationStore.AreBlobsSupported)
                {
                    return _localLocationStore.GetBlobAsync(context, hash);
                }

                return Task.FromResult(new Result<byte[]>("Blobs are not supported."));
            }

            if (_redisContentLocationStore.AreBlobsSupported)
            {
                return _redisContentLocationStore.GetBlobAsync(context, hash);
            }

            return Task.FromResult(new Result<byte[]>("Blobs are not supported."));
        }

        #endregion IDistributedLocationStore Members
    }
}
