﻿using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DynamicData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Wabbajack.BuildServer.Models;
using Wabbajack.Common;
using Wabbajack.Lib.Downloaders;
using Wabbajack.VirtualFileSystem;

namespace Wabbajack.BuildServer.Controllers
{
    [Route("/indexed_files")]
    public class IndexedFiles : AControllerBase<IndexedFiles>
    {
        public IndexedFiles(ILogger<IndexedFiles> logger, DBContext db) : base(logger, db)
        {
        }

        [HttpGet]
        [Route("{xxHashAsBase64}/meta.ini")]
        public async Task<IActionResult> GetFileMeta(string xxHashAsBase64)
        {
            var id = xxHashAsBase64.FromHex().ToBase64();
            var state = await Db.DownloadStates.AsQueryable()
                .Where(d => d.Hash == id && d.IsValid)
                .OrderByDescending(d => d.LastValidationTime)
                .Take(1)
                .ToListAsync();

            if (state.Count == 0)
                return NotFound();
            Response.ContentType = "text/plain";
            return Ok(string.Join("\r\n", state.FirstOrDefault().State.GetMetaIni()));
        }

        [Authorize]
        [HttpDelete]
        [Route("/indexed_files/nexus/{Game}/mod/{ModId}")]
        public async Task<IActionResult> PurgeBySHA256(string Game, string ModId)
        {
            var files = await Db.DownloadStates.AsQueryable().Where(d => d.State is NexusDownloader.State &&
                                                                   ((NexusDownloader.State)d.State).GameName == Game &&
                                                                   ((NexusDownloader.State)d.State).ModID == ModId)
                .ToListAsync();

            async Task DeleteParentsOf(HashSet<string> acc, string hash)
            {
                var parents = await Db.IndexedFiles.AsQueryable().Where(f => f.Children.Any(c => c.Hash == hash))
                    .ToListAsync();

                foreach (var parent in parents)
                    await DeleteThisAndAllChildren(acc, parent.Hash);
            }
            
            async Task DeleteThisAndAllChildren(HashSet<string> acc, string hash)
            {
                acc.Add(hash);
                var children = await Db.IndexedFiles.AsQueryable().Where(f => f.Hash == hash).FirstOrDefaultAsync();
                if (children == null) return;
                foreach (var child in children.Children)
                {
                    await DeleteThisAndAllChildren(acc, child.Hash);
                }

            }
            
            var acc = new HashSet<string>();
            foreach (var file in files)
                await DeleteThisAndAllChildren(acc, file.Hash);

            var acclst = acc.ToList();
            await Db.DownloadStates.DeleteManyAsync(d => acc.Contains(d.Hash));
            await Db.IndexedFiles.DeleteManyAsync(d => acc.Contains(d.Hash));

            return Ok(acc.ToList());
        }

        [HttpGet]
        [Route("{xxHashAsBase64}")]
        public async Task<IActionResult> GetFile(string xxHashAsBase64)
        {
            var id = xxHashAsBase64.FromHex().ToBase64();
            var query = new[]
            {
                new BsonDocument("$match",
                    new BsonDocument("_id", id)),
                new BsonDocument("$graphLookup",
                    new BsonDocument
                    {
                        {"from", "indexed_files"},
                        {"startWith", "$Children.Hash"},
                        {"connectFromField", "Hash"},
                        {"connectToField", "_id"},
                        {"as", "ChildFiles"},
                        {"maxDepth", 8},
                        {"restrictSearchWithMatch", new BsonDocument()}
                    }),
                new BsonDocument("$project", 
                new BsonDocument
                {
                    // If we return all fields some BSAs will return more that 16MB which is the
                    // maximum doc size that can can be returned from MongoDB
                    { "_id", 1 }, 
                    { "Size", 1 }, 
                    { "Children.Name", 1 }, 
                    { "Children.Hash", 1 }, 
                    { "ChildFiles._id", 1 }, 
                    { "ChildFiles.Size", 1 }, 
                    { "ChildFiles.Children.Name", 1 }, 
                    { "ChildFiles.Children.Hash", 1 }, 
                    { "ChildFiles.ChildFiles._id", 1 }, 
                    { "ChildFiles.ChildFiles.Size", 1 }, 
                    { "ChildFiles.ChildFiles.Children.Name", 1 }, 
                    { "ChildFiles.ChildFiles.Children.Hash", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles._id", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.Size", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.Children.Name", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.Children.Hash", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.ChildFiles._id", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.ChildFiles.Size", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.ChildFiles.Children.Name", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.ChildFiles.Children.Hash", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.ChildFiles.ChildFiles._id", 1 }, 
                    { "ChildFiles.ChildFiles.ChildFiles.ChildFiles.ChildFiles.Size", 1 }
                })
            };

            var result = await Db.IndexedFiles.AggregateAsync<TreeResult>(query);

            IndexedVirtualFile Convert(TreeResult t, string Name = null)
            {
                if (t == null)
                    return null;

                Dictionary<string, TreeResult> indexed_children = new Dictionary<string, TreeResult>();
                if (t.ChildFiles != null && t.ChildFiles.Count > 0)
                    indexed_children = t.ChildFiles.ToDictionary(t => t.Hash);

                var file = new IndexedVirtualFile
                {
                    Name = Name,
                    Size = t.Size,
                    Hash = t.Hash,
                    Children = t.ChildFiles != null
                        ? t.Children.Select(child => Convert(indexed_children[child.Hash], child.Name)).ToList()
                        : new List<IndexedVirtualFile>()
                };
                return file;
            }

            var first = result.FirstOrDefault();
            if (first == null)
                return NotFound();
            return Ok(Convert(first));
        }

        public class TreeResult : IndexedFile
        {
            public List<TreeResult> ChildFiles { get; set; }
        }
    }
}
