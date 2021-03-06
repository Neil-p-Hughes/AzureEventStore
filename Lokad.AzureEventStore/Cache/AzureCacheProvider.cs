﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lokad.AzureEventStore.Projections;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Lokad.AzureEventStore.Cache
{
    /// <summary>Intended to persist the state of Priceforge, in order to speed-up the reboot.</summary>
    /// <remarks>
    /// Reading can be specifically disabled in case when the serialization/deserialization
    /// cycle of the cache would lead to a corrupted state.
    /// </remarks>
    public sealed class AzureCacheProvider : IProjectionCacheProvider
    {
        private readonly CloudBlobContainer _container;
        private readonly bool _useStateCache;

        public AzureCacheProvider(CloudBlobContainer container, bool useStateCache)
        {
            _container = container;
            _useStateCache = useStateCache;
        }

        private async Task<CloudBlockBlob[]> Blobs(string fullname)
        {
            await _container.CreateIfNotExistsAsync();

            var result = new List<CloudBlockBlob>();

            var ct = default(BlobContinuationToken);
            while (true)
            {
                var list = await _container.ListBlobsSegmentedAsync(
                    fullname,
                    /* useFlatBlobListing: */true,
                    BlobListingDetails.None,
                    /* max results */ null, ct,
                    new BlobRequestOptions(),
                    new OperationContext());

                ct = list.ContinuationToken;

                foreach (var item in list.Results)
                {
                    var blob = item as CloudBlockBlob;
                    if (blob == null) continue;

                    result.Add(blob);
                }

                if (ct == null) break;
            }

            return result.OrderBy(b => b.Name).ToArray();
        }

        public async Task<Stream> OpenReadAsync(string fullname)
        {
            if (!_useStateCache) return null;

            var all = await Blobs(fullname);
            if (all.Length == 0) return null;
            
            return await all[all.Length - 1].OpenReadAsync();            
        }

        public async Task<Stream> OpenWriteAsync(string fullname)
        {
            var all = await Blobs(fullname);

            var name = fullname + "/" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            // Not the latest: don't bother writing.
            if (all.Length > 0 && string.CompareOrdinal(all[all.Length - 1].Name, name) >= 0)
                return null;

            // Clean-up the old and deprecated versions of the cache
            if (all.Length > 3)
            {
                for (var i = 0; i < all.Length - 3; ++i)
                {
                    await _container.GetBlockBlobReference(all[i].Name).DeleteAsync();
                }
            }

            var blob = _container.GetBlockBlobReference(name);
            return await blob.OpenWriteAsync(
                AccessCondition.GenerateIfNotExistsCondition(),
                new BlobRequestOptions(),
                new OperationContext());
        }
    }
}
