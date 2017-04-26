﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using CsvHelper;
using Facebook;
using FacebookCivicInsights.Data;
using FacebookCivicInsights.Models;
using FacebookCivicInsights.Data.Scraper;
using FacebookCivicInsights.Data.Importer;
using Microsoft.AspNetCore.Mvc;
using Elasticsearch.Net;

namespace FacebookCivicInsights.Controllers.Dashboard
{
    [Route("/api/dashboard/scrape/post")]
    public class PostScrapeController : Controller
    {
        private PostScraper PostScraper { get; }
        private CommentScraper CommentScraper { get; }
        private PageScraper PageScraper { get; }
        private ElasticSearchRepository<PageMetadata> PageMetadataRepository { get; }
        private ElasticSearchRepository<PostScrapeHistory> PostScrapeHistoryRepository { get; }

        public PostScrapeController(PostScraper postScraper, CommentScraper commentScraper, PageScraper pageScraper, ElasticSearchRepository<PageMetadata> pageMetadataRepository, ElasticSearchRepository<PostScrapeHistory> postScrapeHistoryRepository)
        {
            PostScraper = postScraper;
            CommentScraper = commentScraper;
            PageScraper = pageScraper;
            PageMetadataRepository = pageMetadataRepository;
            PostScrapeHistoryRepository = postScrapeHistoryRepository;
        }

        [HttpGet("{id}")]
        public ScrapedPost GetPost(string id) => PostScraper.Get(id);

        [HttpGet("all")]
        public PagedResponse AllPosts(int pageNumber, int pageSize, OrderingType? order, DateTime? since, DateTime? until)
        {
            return PostScraper.All<TimeSearchResponse<ScrapedPost>, ScrapedPost>(
                new PagedResponse(pageNumber, pageSize),
                new Ordering<ScrapedPost>("created_time", order),
                p => p.CreatedTime, since, until);
        }

        [HttpGet("export")]
        public IActionResult ExportPost(OrderingType? order, DateTime? since, DateTime? until)
        {
            var ordering = new Ordering<ScrapedPost>("created_time", order);
            byte[] serialized = PostScraper.Export(ordering, p => p.CreatedTime, since, until, CsvSerialization.MapPost);
            return File(serialized, "text/csv", "export.csv");
        }

        public class PostScrapeRequest
        {
            public IEnumerable<string> Pages { get; set; }
            public DateTime Since { get; set; }
            public DateTime Until { get; set; }
        }

        [HttpPost("scrape")]
        public PostScrapeHistory ScrapePosts([FromBody]PostScrapeRequest request)
        {
            Debug.Assert(request != null);
            Console.WriteLine("Started Scraping");

            // If no specific pages were specified, scrape them all.
            PageMetadata[] pages;
            if (request.Pages == null)
            {
                pages = PageMetadataRepository.Paged().AllData().ToArray();
            }
            else
            {
                pages = request.Pages.Select(p => PageMetadataRepository.Get(p)).ToArray();
            }

            int numberOfComments = 0;
            ScrapedPost[] posts = PostScraper.Scrape(pages, request.Since, request.Until).ToArray();
            foreach (ScrapedPost post in posts)
            {
                ScrapedComment[] comments = CommentScraper.Scrape(post).ToArray();
                numberOfComments += comments.Length;
                Console.WriteLine(numberOfComments);
            }
            Console.WriteLine("Done Scraping");

            var postScrape = new PostScrapeHistory
            {
                Id = Guid.NewGuid().ToString(),
                Since = request.Since,
                Until = request.Until,
                ImportStart = posts.FirstOrDefault()?.Scraped ?? DateTime.Now,
                ImportEnd = DateTime.Now,
                NumberOfPosts = posts.Length,
                NumberOfComments = numberOfComments,
                Pages = pages
            };

            return PostScrapeHistoryRepository.Save(postScrape);
        }

        [HttpGet("import/historical")]
        public IEnumerable<ScrapedPost> ImportHistoricalPosts()
        {
            var importer = new ScrapeImporter(PageScraper, PageMetadataRepository, PostScraper);
            IEnumerable<string> files = Directory.EnumerateFiles("C:\\Users\\hughb\\Documents\\TAF\\Data", "*.csv", SearchOption.AllDirectories);
            IEnumerable<string> fanCountFiles = files.Where(f => f.Contains("DedooseChartExcerpts"));

            return importer.ImportPosts(fanCountFiles);
        }

        [HttpGet("import/elasticsearch")]
        public IEnumerable<ScrapedPost> ImportElasticSearchPosts(string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Open))
            using (var streamReader = new StreamReader(fileStream))
            using (var csvReader = new CsvReader(streamReader))
            {
                csvReader.Configuration.RegisterClassMap<ScrapedPostMapping>();
                foreach (ScrapedPost record in csvReader.GetRecords<ScrapedPost>())
                {
                    yield return PostScraper.Save(record, Refresh.False);
                }
            }
        }

        [HttpGet("history/{id}")]
        public PostScrapeHistory GetScrape(string id) => PostScrapeHistoryRepository.Get(id);

        [HttpGet("history/all")]
        public PagedResponse AllScrapes(int pageNumber, int pageSize, OrderingType? order, DateTime? since, DateTime? until)
        {
            return PostScrapeHistoryRepository.All<TimeSearchResponse<PostScrapeHistory>, PostScrapeHistory>(
                new PagedResponse(pageNumber, pageSize),
                new Ordering<PostScrapeHistory>("importStart", order),
                p => p.ImportStart, since, until);
        }
    }
}
