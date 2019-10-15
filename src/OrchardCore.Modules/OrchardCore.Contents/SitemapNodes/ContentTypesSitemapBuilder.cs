using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using OrchardCore.ContentManagement;
using OrchardCore.ContentManagement.Metadata;
using OrchardCore.ContentManagement.Records;
using OrchardCore.Sitemaps.Builders;
using OrchardCore.Sitemaps.Models;
using YesSql;
using YesSql.Services;

namespace OrchardCore.Contents.SitemapNodes
{
    public class ContentTypesSitemapBuilder : SitemapBuilderBase<ContentTypesSitemap>
    {
        private static readonly XNamespace Namespace = "http://www.sitemaps.org/schemas/sitemap/0.9";

        private readonly ISession _session;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IContentManager _contentManager;

        public ContentTypesSitemapBuilder(
            ISession session,
            IContentDefinitionManager contentDefinitionManager,
            IContentManager contentManager
            )
        {
            _session = session;
            _contentDefinitionManager = contentDefinitionManager;
            _contentManager = contentManager;
        }

        public override async Task<XDocument> BuildSitemapsAsync(ContentTypesSitemap sitemap, SitemapBuilderContext context)
        {
            var contentItems = await GetContentItemsToBuildAsync(sitemap);

            var root = new XElement(Namespace + "urlset");

            foreach (var contentItem in contentItems)
            {
                var url = new XElement(Namespace + "url");

                if (await BuildUrlsetMetadataAsync(sitemap, context, contentItem, url))
                {
                    root.Add(url);
                }
            }
            var document = new XDocument(root);
            return new XDocument(document);
        }

        public override async Task<DateTime?> GetLastModifiedDateAsync(ContentTypesSitemap sitemap, SitemapBuilderContext context)
        {
            ContentItem mostRecentModifiedContentItem;

            var typesToIndex = _contentDefinitionManager.ListTypeDefinitions()
                .Where(ctd => sitemap.ContentTypes.ToList()
                .Any(s => ctd.Name == s.ContentTypeName))
                .Select(x => x.Name);

            var contentTypes = sitemap.ContentTypes.Select(x => x.ContentTypeName);
            // This is just an estimate, so doesn't take into account Take/Skip values.
            var query = _session.Query<ContentItem>()
                .With<ContentItemIndex>(x => x.ContentType.IsIn(typesToIndex) && x.Published)
                .OrderByDescending(x => x.ModifiedUtc);
            mostRecentModifiedContentItem = await query.FirstOrDefaultAsync();

            return mostRecentModifiedContentItem.ModifiedUtc;
        }

        private async Task<bool> BuildUrlsetMetadataAsync(ContentTypesSitemap sitemap,
            SitemapBuilderContext context,
            ContentItem contentItem,
            XElement url)
        {
            if (await BuildUrlAsync(context, contentItem, url))
            {
                BuildLastMod(contentItem, url);
                BuildChangeFrequencyPriority(sitemap, contentItem, url);
                return true;
            };

            return false;
        }

        private async Task<IEnumerable<ContentItem>> GetContentItemsToBuildAsync(ContentTypesSitemap sitemap)
        {
            var contentItems = new List<ContentItem>();

            // Should allow selection of multiple content types, though recommendation when
            // splitting a sitemap is to restrict to a single content item. 
            //TODO make a note in readme -> large content item recommendations
            if (sitemap.ContentTypes.Any(x => !x.TakeAll))
            {
                // Process content types that have a skip/take value.
                var tasks = new List<Task<IEnumerable<ContentItem>>>();
                foreach (var takeSomeType in sitemap.ContentTypes.Where(x => !x.TakeAll))
                {
                    var query = _session.Query<ContentItem>()
                        .With<ContentItemIndex>(x => x.ContentType == takeSomeType.ContentTypeName && x.Published)
                        .OrderByDescending(x => x.ModifiedUtc)
                        .Skip(takeSomeType.Skip)
                        .Take(takeSomeType.Take);
                    tasks.Add(query.ListAsync());
                }
                // Process content types without skip/take value.
                var typesToIndex = _contentDefinitionManager.ListTypeDefinitions()
                    .Where(ctd => sitemap.ContentTypes.Where(x => x.TakeAll).Any(s => ctd.Name == s.ContentTypeName))
                    .Select(x => x.Name);
                var othersQuery = _session.Query<ContentItem>()
                    .With<ContentItemIndex>(x => x.ContentType.IsIn(typesToIndex) && x.Published)
                    .OrderByDescending(x => x.ModifiedUtc);

                tasks.Add(othersQuery.ListAsync());
                await Task.WhenAll(tasks);
                tasks.ForEach(x => contentItems.AddRange(x.Result));
            }
            else
            {
                var typeDef = _contentDefinitionManager.ListTypeDefinitions();
                var typesTo = _contentDefinitionManager.ListTypeDefinitions()
                   .Where(ctd => sitemap.ContentTypes.ToList().Any(s => ctd.Name == s.ContentTypeName))
                   .Select(t => t.Name);

                var typesToIndex = _contentDefinitionManager.ListTypeDefinitions()
                   .Where(ctd => sitemap.ContentTypes.Any(s => ctd.Name == s.ContentTypeName))
                   .Select(x => x.Name);

                var query = _session.Query<ContentItem>()
                    .With<ContentItemIndex>(x => x.ContentType.IsIn(typesToIndex) && x.Published)
                    .OrderByDescending(x => x.ModifiedUtc);

                contentItems = (await query.ListAsync()).ToList();
            }

            return contentItems;
        }

        private async Task<bool> BuildUrlAsync(SitemapBuilderContext context, ContentItem contentItem, XElement url)
        {
            // TODO resolve this dependance on SitemapPart through
            // a Sitemap Content Item Validator

            // SitemapPart is optional, but to exclude or override defaults add it to the ContentItem
            var sitemapPart = contentItem.As<SitemapPart>();
            if (sitemapPart != null && sitemapPart.OverrideSitemapConfig && sitemapPart.Exclude)
            {
                return false;
            }

            var contentItemMetadata = await _contentManager.PopulateAspectAsync<ContentItemMetadata>(contentItem);
            var routes = contentItemMetadata.DisplayRouteValues;

            // UrlHelper.Action includes BasePath automatically if present.
            // If content item is assigned as home route, Urlhelper resolves as site root.
            var locValue = context.HostPrefix + context.UrlHelper.Action(routes["Action"].ToString(), routes);

            var loc = new XElement(Namespace + "loc");
            loc.Add(locValue);
            url.Add(loc);
            return true;
        }

        private void BuildChangeFrequencyPriority(ContentTypesSitemap sitemap, ContentItem contentItem, XElement url)
        {
            string changeFrequencyValue = null;
            string priorityValue = null;
            var sitemapEntry = sitemap.ContentTypes.FirstOrDefault(x => x.ContentTypeName == contentItem.ContentType);
            changeFrequencyValue = sitemapEntry.ChangeFrequency.ToString();
            priorityValue = sitemapEntry.Priority.ToString();
            if (contentItem.Has<SitemapPart>())
            {
                var part = contentItem.As<SitemapPart>();
                if (part.OverrideSitemapConfig)
                {
                    changeFrequencyValue = part.ChangeFrequency.ToString();
                    priorityValue = part.Priority.ToString();
                }
            }

            var changeFreq = new XElement(Namespace + "changefreq");
            changeFreq.Add(changeFrequencyValue.ToLower());
            url.Add(changeFreq);

            var priority = new XElement(Namespace + "priority");
            priority.Add(priorityValue);
            url.Add(priority);
        }

        private void BuildLastMod(ContentItem contentItem, XElement url)
        {
            var lastMod = new XElement(Namespace + "lastmod");
            lastMod.Add(contentItem.ModifiedUtc.GetValueOrDefault().ToString("yyyy-MM-ddTHH:mm:sszzz"));
            url.Add(lastMod);
        }
    }
}
