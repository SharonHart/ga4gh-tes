﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Tes.Repository;

namespace TesApi.Web
{
    /// <summary>
    /// Implements caching and retries for <see cref="IRepository{T}"/>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class CachingWithRetriesRepository<T> : IRepository<T> where T : RepositoryItem<T>
    {
        private readonly IRepository<T> repository;
        private readonly object cacheLock = new();
        private readonly IMemoryCache cache = new MemoryCache(new MemoryCacheOptions());
        private readonly IList<object> itemsPredicateCachedKeys = new List<object>();
        private readonly Management.CacheAndRetryHandler cacheAndRetryHandler;

        /// <summary>
        /// Constructor to create a cache and retry wrapper for <see cref="IRepository{T}"/>
        /// </summary>
        /// <param name="repository"><see cref="IRepository{T}"/> to wrap with caching and retries</param>
        /// <param name="cacheAndRetryHandler"></param>
        public CachingWithRetriesRepository(IRepository<T> repository, Management.CacheAndRetryHandler cacheAndRetryHandler)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ArgumentNullException.ThrowIfNull(cacheAndRetryHandler);

            this.cacheAndRetryHandler = cacheAndRetryHandler;
            this.repository = repository;
        }

        /// <inheritdoc/>
        public async Task<T> CreateItemAsync(T item)
        {
            var repositoryItem = await cacheAndRetryHandler.ExecuteWithRetryAsync(() => repository.CreateItemAsync(item));
            ClearAllItemsPredicateCachedKeys();
            return repositoryItem;
        }

        /// <inheritdoc/>
        public async Task DeleteItemAsync(string id)
        {
            if (cache.TryGetValue(id, out var cachedRepositoryItem))
            {
                cache.Remove(id);
            }

            await cacheAndRetryHandler.ExecuteWithRetryAsync(() => repository.DeleteItemAsync(id));
            ClearAllItemsPredicateCachedKeys();
        }

        /// <inheritdoc/>
        public async Task<bool> TryGetItemAsync(string id, Action<T> onSuccess)
        {
            if (cache.TryGetValue(id, out T repositoryItem))
            {
                onSuccess(repositoryItem);
                return true;
            }

            var repositoryItemFound = await cacheAndRetryHandler.ExecuteWithRetryAsync(() => repository.TryGetItemAsync(id, item => repositoryItem = item));

            if (repositoryItemFound)
            {
                cache.Set(id, repositoryItem, TimeSpan.FromMinutes(5));
                onSuccess(repositoryItem);
            }

            return repositoryItemFound;
        }

        /// <inheritdoc/>
        public Task<(string, IEnumerable<T>)> GetItemsAsync(Expression<Func<T, bool>> predicate, int pageSize, string continuationToken)
            => cacheAndRetryHandler.ExecuteWithRetryAsync(() => repository.GetItemsAsync(predicate, pageSize, continuationToken));

        /// <inheritdoc/>
        public async Task<IEnumerable<T>> GetItemsAsync(Expression<Func<T, bool>> predicate)
        {
            var key = predicate.ToString().GetHashCode();
            IEnumerable<T> repositoryItems = new List<T>();

            if (cache.TryGetValue(key, out repositoryItems))
            {
                return repositoryItems;
            }

            repositoryItems = await cacheAndRetryHandler.ExecuteWithRetryAsync(() => repository.GetItemsAsync(predicate));

            lock (cacheLock)
            {
                cache.Set(key, repositoryItems, DateTimeOffset.MaxValue);
                itemsPredicateCachedKeys.Add(key);
            }

            return repositoryItems;
        }

        /// <inheritdoc/>
        public async Task<T> UpdateItemAsync(T item)
        {
            var id = item.GetId();

            if (cache.TryGetValue(id, out var _))
            {
                cache.Remove(id);
            }

            var repositoryItem = await cacheAndRetryHandler.ExecuteWithRetryAsync(() => repository.UpdateItemAsync(item));
            ClearAllItemsPredicateCachedKeys();
            return repositoryItem;
        }

        private void ClearAllItemsPredicateCachedKeys()
        {
            lock (cacheLock)
            {
                foreach (var key in itemsPredicateCachedKeys)
                {
                    cache.Remove(key);
                }

                itemsPredicateCachedKeys.Clear();
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            cache?.Dispose();
            repository?.Dispose();
        }
    }
}
