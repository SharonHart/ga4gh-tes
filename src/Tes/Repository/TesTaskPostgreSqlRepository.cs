﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Tes.Repository
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Polly;
    using Tes.Models;
    using Tes.Utilities;

    /// <summary>
    /// A TesTask specific repository for storing the TesTask as JSON within an Entity Framework Postgres table
    /// </summary>
    /// <typeparam name="TesTask"></typeparam>
    public sealed class TesTaskPostgreSqlRepository : IRepository<TesTask>
    {
        private static readonly TimeSpan defaultCompletedTaskCacheExpiration = TimeSpan.FromDays(1);
        private readonly Func<TesDbContext> createDbContext;
        private readonly ICache<TesTask> cache;
        private readonly ILogger logger;

        /// <summary>
        /// Default constructor that also will create the schema if it does not exist
        /// </summary>
        /// <param name="connectionString">The PostgreSql connection string</param>
        public TesTaskPostgreSqlRepository(IOptions<PostgreSqlOptions> options, ILogger<TesTaskPostgreSqlRepository> logger, ICache<TesTask> cache = null)
        {
            this.cache = cache;
            this.logger = logger;
            var connectionString = new ConnectionStringUtility().GetPostgresConnectionString(options);
            createDbContext = () => { return new TesDbContext(connectionString); };
            using var dbContext = createDbContext();
            dbContext.Database.MigrateAsync().Wait();
            WarmCacheAsync(CancellationToken.None).Wait();
        }

        /// <summary>
        /// Constructor for testing to enable mocking DbContext 
        /// </summary>
        /// <param name="createDbContext">A delegate that creates a TesTaskPostgreSqlRepository context</param>
        public TesTaskPostgreSqlRepository(Func<TesDbContext> createDbContext)
        {
            this.createDbContext = createDbContext;
            using var dbContext = createDbContext();
            dbContext.Database.MigrateAsync().Wait();
        }

        private async Task WarmCacheAsync(CancellationToken cancellationToken)
        {
            if (cache == null)
            {
                logger.LogWarning("Cache is null for TesTaskPostgreSqlRepository; no caching will be used.");
                return;
            }

            var sw = Stopwatch.StartNew();
            logger.LogInformation("Warming cache...");

            // Don't allow the state of the system to change until the cache and system are consistent;
            // this is a fast PostgreSQL query even for 1 million items
            await Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(3,
                    retryAttempt =>
                    {
                        logger.LogWarning($"Warming cache retry attempt #{retryAttempt}");
                        return TimeSpan.FromSeconds(10);
                    },
                    (ex, ts) =>
                    {
                        logger.LogCritical(ex, "Couldn't warm cache, is the database online?");
                    })
                .ExecuteAsync(async () =>
                {
                    var activeTasks = await GetItemsAsync(task => TesTask.ActiveStates.Contains(task.State), cancellationToken);
                    var tasksAddedCount = 0;

                    foreach (var task in activeTasks.OrderBy(t => t.CreationTime))
                    {
                        cache?.TryAdd(task.Id, task);
                        tasksAddedCount++;
                    }

                    logger.LogInformation($"Cache warmed successfully in {sw.Elapsed.TotalSeconds:n3} seconds. Added {tasksAddedCount:n0} items to the cache.");
                });
        }


        /// <summary>
        /// Get a TesTask by the TesTask.ID
        /// </summary>
        /// <param name="id">The TesTask's ID</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <param name="onSuccess">Delegate to be invoked on success</param>
        public async Task<bool> TryGetItemAsync(string id, CancellationToken cancellationToken, Action<TesTask> onSuccess = null)
        {
            if (cache?.TryGetValue(id, out var task) == true)
            {
                onSuccess?.Invoke(task);
                return true;
            }

            using var dbContext = createDbContext();

            // Search for Id within the JSON
            var item = await dbContext.TesTasks.FirstOrDefaultAsync(t => t.Json.Id == id, cancellationToken: cancellationToken);

            if (item is null)
            {
                return false;
            }

            onSuccess?.Invoke(item.Json);
            cache?.TryAdd(id, item.Json);
            return true;
        }

        /// <summary>
        /// Get TesTask items
        /// </summary>
        /// <param name="predicate">Predicate to query the TesTasks</param>
        /// <returns></returns>
        /// <param name="cancellationToken"></param>
        public async Task<IEnumerable<TesTask>> GetItemsAsync(Expression<Func<TesTask, bool>> predicate, CancellationToken cancellationToken)
        {
            using var dbContext = createDbContext();

            // Search for items in the JSON
            var query = dbContext.TesTasks.Select(t => t.Json).Where(predicate);

            //var sqlQuery = query.ToQueryString();
            //Debugger.Break();
            return await query.ToListAsync(cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Encapsulates a TesTask as JSON
        /// </summary>
        /// <param name="item">TesTask to store as JSON in the database</param>
        /// <returns></returns>
        /// <param name="cancellationToken"></param>
        public async Task<TesTask> CreateItemAsync(TesTask item, CancellationToken cancellationToken)
        {
            using var dbContext = createDbContext();
            var dbItem = new TesTaskDatabaseItem { Json = item };
            dbContext.TesTasks.Add(dbItem);
            await dbContext.SaveChangesAsync(cancellationToken);
            cache?.TryAdd(item.Id, item);
            return item;
        }

        /// <summary>
        /// Encapsulates TesTasks as JSON
        /// </summary>
        /// <param name="item">TesTask to store as JSON in the database</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        /// <returns></returns>
        public async Task<List<TesTask>> CreateItemsAsync(List<TesTask> items, CancellationToken cancellationToken)
        {
            using var dbContext = createDbContext();

            foreach (var item in items)
            {
                var dbItem = new TesTaskDatabaseItem { Json = item };
                dbContext.TesTasks.Add(dbItem);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return items;
        }

        /// <summary>
        /// Base class searches within model, this method searches within the JSON
        /// </summary>
        /// <param name="tesTask"></param>
        /// <returns></returns>
        /// <param name="cancellationToken"></param>
        public async Task<TesTask> UpdateItemAsync(TesTask tesTask, CancellationToken cancellationToken)
        {
            using var dbContext = createDbContext();

            // Manually set entity state to avoid potential NPG PostgreSql bug
            dbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            var item = await dbContext.TesTasks.FirstOrDefaultAsync(t => t.Json.Id == tesTask.Id, cancellationToken: cancellationToken);

            if (item is null)
            {
                throw new Exception($"No TesTask with ID {tesTask.Id} found in the database.");
            }

            item.Json = tesTask;

            // Manually set entity state to avoid potential NPG PostgreSql bug
            dbContext.Entry(item).State = EntityState.Modified;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!tesTask.IsActiveState())
            {
                // Remove completed tasks from cache after shorter interval
                cache?.TryUpdate(tesTask.Id, tesTask, defaultCompletedTaskCacheExpiration);
            }
            else
            {
                cache?.TryUpdate(tesTask.Id, tesTask);
            }

            return item.Json;
        }

        /// <summary>
        /// Base class deletes by Item.Id, this method deletes by Item.Json.Id
        /// </summary>
        /// <param name="id">TesTask Id</param>
        /// <returns></returns>
        /// <param name="cancellationToken"></param>
        public async Task DeleteItemAsync(string id, CancellationToken cancellationToken)
        {
            using var dbContext = createDbContext();
            var item = await dbContext.TesTasks.FirstOrDefaultAsync(t => t.Json.Id == id, cancellationToken: cancellationToken);

            if (item is null)
            {
                throw new Exception($"No TesTask with ID {item.Id} found in the database.");
            }

            dbContext.TesTasks.Remove(item);
            await dbContext.SaveChangesAsync(cancellationToken);
            cache?.TryRemove(id);
        }

        /// <summary>
        /// Identical to GetItemsAsync, paging is not supported. All items are returned
        /// </summary>
        /// <param name="predicate">Predicate to query the TesTasks</param>
        /// <param name="pageSize">Ignored and has no effect</param>
        /// <param name="continuationToken">Ignored and has no effect</param>
        /// <returns></returns>
        /// <param name="cancellationToken"></param>
        public async Task<(string, IEnumerable<TesTask>)> GetItemsAsync(Expression<Func<TesTask, bool>> predicate, int pageSize, string continuationToken, CancellationToken cancellationToken)
        {
            // TODO paging support
            var results = await GetItemsAsync(predicate, cancellationToken);
            return (null, results);
        }

        /// <inheritdoc/>
        public ValueTask<bool> TryRemoveItemFromCacheAsync(TesTask item, CancellationToken _1)
        {
            return ValueTask.FromResult(cache?.TryRemove(item.Id) ?? false);
        }

        public void Dispose()
        {

        }
    }
}
