using System.Collections.Concurrent;
using Lucene.Net.Analysis.En;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Microsoft.EntityFrameworkCore;
using RecipeManager.Web.Data;

namespace RecipeManager.Web.Search;

public record SearchHit(int Id, string Title, string CategoryName, int CookingTimeMinutes, float Score);

public record SearchResult(int Total, IReadOnlyList<SearchHit> Hits);

// Full-text recipe search with Lucene.NET. The index is in memory; a kitchen's documents are rebuilt
// from the database on the first search after a change. Queries are limited to one kitchen.
public sealed class RecipeSearchIndex : IDisposable
{
    private const LuceneVersion Version = LuceneVersion.LUCENE_48;

    // Matches in the title count most, then ingredients and category, then the free text.
    private static readonly Dictionary<string, float> Boosts = new()
    {
        ["title"] = 3f, ["ingredient"] = 2f, ["category"] = 2f, ["description"] = 1f, ["instructions"] = 1f
    };

    // EnglishAnalyzer lowercases, drops stop words and stems, so "tomatoes" finds "tomato".
    // Cyrillic words pass through lowercased, without stemming.
    private readonly EnglishAnalyzer _analyzer = new(Version);
    private readonly RAMDirectory _directory = new();
    private readonly IndexWriter _writer;
    private readonly SearcherManager _searchers;
    private readonly SemaphoreSlim _building = new(1, 1);

    // A kitchen's change counter and the counter its documents were built at.
    private readonly ConcurrentDictionary<int, long> _changes = new();
    private readonly ConcurrentDictionary<int, long> _built = new();

    public RecipeSearchIndex()
    {
        _writer = new IndexWriter(_directory, new IndexWriterConfig(Version, _analyzer));
        _searchers = new SearcherManager(_writer, applyAllDeletes: true, searcherFactory: null);
    }

    // Called after a save that touched recipes, categories, ingredients or their links.
    public void MarkChanged(int tenantId) => _changes.AddOrUpdate(tenantId, 1, (_, count) => count + 1);

    // Throws ParseException for a query Lucene cannot parse.
    public async Task<SearchResult> SearchAsync(ApplicationDbContext context, int tenantId, string text,
        int? maxMinutes, int skip, int limit, CancellationToken cancellationToken)
    {
        var parser = new MultiFieldQueryParser(Version, [.. Boosts.Keys], _analyzer, Boosts)
        {
            DefaultOperator = Operator.AND
        };
        // A query of stop words only ("the", "and") leaves nothing to look for.
        var parsed = parser.Parse(text);
        if (parsed is null or BooleanQuery { Clauses.Count: 0 }) return new SearchResult(0, []);
        var query = new BooleanQuery
        {
            { parsed, Occur.MUST },
            { new TermQuery(new Term("tenant", tenantId.ToString())), Occur.MUST }
        };
        if (maxMinutes != null)
            query.Add(NumericRangeQuery.NewInt32Range("minutes", null, maxMinutes, true, true), Occur.MUST);

        await EnsureBuiltAsync(context, tenantId, cancellationToken);
        var searcher = _searchers.Acquire();
        try
        {
            var top = searcher.Search(query, skip + limit);
            var hits = top.ScoreDocs.Skip(skip).Select(scoreDoc =>
            {
                var document = searcher.Doc(scoreDoc.Doc);
                return new SearchHit(int.Parse(document.Get("id")), document.Get("title"), document.Get("category"),
                    document.GetField("minutes").GetInt32Value() ?? 0, scoreDoc.Score);
            }).ToList();
            return new SearchResult(top.TotalHits, hits);
        }
        finally
        {
            _searchers.Release(searcher);
        }
    }

    private async Task EnsureBuiltAsync(ApplicationDbContext context, int tenantId, CancellationToken cancellationToken)
    {
        if (IsCurrent(tenantId)) return;
        await _building.WaitAsync(cancellationToken);
        try
        {
            if (IsCurrent(tenantId)) return;
            // Read the counter before the data: a save during the rebuild leaves the kitchen stale again.
            var version = _changes.GetValueOrDefault(tenantId);
            // The request context is already filtered to this kitchen.
            var recipes = await context.Recipes.AsNoTracking()
                .Include(recipe => recipe.Category)
                .Include(recipe => recipe.RecipeIngredients).ThenInclude(item => item.Ingredient)
                .ToListAsync(cancellationToken);

            var tenant = tenantId.ToString();
            _writer.DeleteDocuments(new Term("tenant", tenant));
            foreach (var recipe in recipes)
            {
                var document = new Document
                {
                    new StringField("id", recipe.Id.ToString(), Field.Store.YES),
                    new StringField("tenant", tenant, Field.Store.NO),
                    new TextField("title", recipe.Title, Field.Store.YES),
                    new TextField("category", recipe.Category.Name, Field.Store.YES),
                    new TextField("description", recipe.Description ?? string.Empty, Field.Store.NO),
                    new TextField("instructions", recipe.Instructions, Field.Store.NO),
                    new Int32Field("minutes", recipe.CookingTimeMinutes, Field.Store.YES)
                };
                foreach (var item in recipe.RecipeIngredients)
                    document.Add(new TextField("ingredient", item.Ingredient.Name, Field.Store.NO));
                _writer.AddDocument(document);
            }
            _searchers.MaybeRefreshBlocking();
            _built[tenantId] = version;
        }
        finally
        {
            _building.Release();
        }
    }

    private bool IsCurrent(int tenantId) =>
        _built.TryGetValue(tenantId, out var built) && built == _changes.GetValueOrDefault(tenantId);

    public void Dispose()
    {
        _searchers.Dispose();
        _writer.Dispose();
        _directory.Dispose();
        _analyzer.Dispose();
    }
}
