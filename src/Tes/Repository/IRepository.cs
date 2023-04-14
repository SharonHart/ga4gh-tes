﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tes.Repository
{
    /// <summary>
    /// A general repository interface for persistence of T instances
    /// </summary>
    /// <typeparam name="T">The type of the instance</typeparam>
    public interface IRepository<T> : IDisposable where T : RepositoryItem<T>
    {
        /// <summary>
        /// Create a new item
        /// </summary>
        /// <param name="item">The item to create.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        /// <returns>The created item</returns>
        Task<T> CreateItemAsync(T item, CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete an existing item
        /// </summary>
        /// <param name="id">The ID of the item to delete.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        Task DeleteItemAsync(string id, CancellationToken cancellationToken = default);

        /// <summary>
        /// Get an item by ID
        /// </summary>
        /// <param name="id">The ID of the item to retrieve.</param>
        /// <param name="onSuccess">The action to run when the item with the ID is found.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        /// <returns>The item instance.</returns>
        Task<bool> TryGetItemAsync(string id, Action<T> onSuccess = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a collection of items from the repository
        /// </summary>
        /// <param name="predicate">The 'where' clause.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        /// <returns>The collection of retrieved items.</returns>
        Task<IEnumerable<T>> GetItemsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads a collection of items from the repository
        /// </summary>
        /// <param name="predicate">The 'where' clause.</param>
        /// <param name="pageSize">The max number of items to retrieve.</param>
        /// <param name="continuationToken">A token to continue retrieving items if the max is returned.</param>
        /// <returns>A continuation token string, and the collection of retrieved items.</returns>
        Task<(string, IEnumerable<T>)> GetItemsAsync(Expression<Func<T, bool>> predicate, int pageSize, string continuationToken, CancellationToken cancellationToken = default);

        /// <summary>
        /// Update the item in the repository
        /// </summary>
        /// <param name="id">The ID of the item to update.</param>
        /// <param name="item">The item to persist.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> for controlling the lifetime of the asynchronous operation.</param>
        /// <returns>The created document.</returns>
        Task<T> UpdateItemAsync(T item, CancellationToken cancellationToken = default);
    }
}
