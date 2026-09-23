using System.Collections.ObjectModel;
using FotoArchiv.App.Models;

namespace FotoArchiv.App.Services;

public sealed class DuplicateDetectionService
{
    public async Task<IReadOnlyList<DuplicateGroup>> DetectAsync(
        IReadOnlyList<MediaItem> media,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var item in media)
        {
            item.DuplicateConfidence = DuplicateConfidence.None;
            item.DuplicateGroupId = null;
            item.IsRecommendedKeep = false;
            item.WillKeep = true;
        }

        var candidates = media.Where(item => item.Kind != MediaKind.Sidecar).ToList();
        var exactGroups = await FindExactGroupsAsync(candidates, progress, cancellationToken);
        var result = new List<DuplicateGroup>();
        var nextId = 1;

        foreach (var matches in exactGroups)
        {
            var group = CreateGroup(nextId++, DuplicateConfidence.Exact,
                "Obsah souboru je shodný bit po bitu (SHA-256).", matches);
            result.Add(group);
        }

        var exactRepresentatives = exactGroups.ToDictionary(
            group => group[0],
            group => QualityRanker.SelectBest(group));
        var exactMembers = exactGroups.SelectMany(group => group).ToHashSet();
        var visualCandidates = candidates
            .Where(item => item.Kind == MediaKind.Image && !exactMembers.Contains(item))
            .Concat(exactRepresentatives.Values.Where(item => item.Kind == MediaKind.Image))
            .Distinct()
            .ToList();

        progress?.Report("Počítám obrazové otisky…");
        await Parallel.ForEachAsync(visualCandidates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2)
            },
            async (item, token) => item.PerceptualHash = await HashService.ComputePerceptualHashAsync(item, token));

        foreach (var visualGroup in FindVisualGroups(visualCandidates, cancellationToken))
        {
            var confidence = visualGroup.MaxDistance <= 2 &&
                             visualGroup.Items.Select(item => (item.Width, item.Height)).Distinct().Count() == 1 &&
                             visualGroup.Items.Select(item => item.NormalizedPixelHash).Distinct().Count() == 1 &&
                             visualGroup.Items.All(item => item.NormalizedPixelHash is not null) &&
                             SizeRatio(visualGroup.Items) >= 0.80
                ? DuplicateConfidence.High
                : DuplicateConfidence.Low;

            var reason = confidence == DuplicateConfidence.High
                ? "Obrazový otisk, rozlišení a množství obrazových dat se téměř shodují; metadata nebo datum se mohou lišit."
                : "Snímky jsou vizuálně podobné, ale shodu musí potvrdit uživatel.";

            result.Add(CreateGroup(nextId++, confidence, reason, visualGroup.Items));
        }

        var alreadyVisual = result
            .Where(group => group.Confidence is DuplicateConfidence.High or DuplicateConfidence.Low)
            .SelectMany(group => group.Items)
            .ToHashSet();

        var attributeGroups = visualCandidates
            .Where(item => !alreadyVisual.Contains(item))
            .GroupBy(item => new { item.FileSize, item.Width, item.Height, item.Extension })
            .Where(group => group.Count() > 1 && group.Select(item => item.Sha256).Distinct().Count() > 1);

        foreach (var attributeGroup in attributeGroups)
        {
            result.Add(CreateGroup(nextId++, DuplicateConfidence.Low,
                "Shoduje se velikost na bajty, rozlišení a formát, ale obrazový obsah nebyl potvrzen.",
                attributeGroup.ToList()));
        }

        return result
            .OrderByDescending(group => group.Confidence)
            .ThenByDescending(group => group.Items.Count)
            .ToList();
    }

    private static async Task<List<List<MediaItem>>> FindExactGroupsAsync(
        IReadOnlyList<MediaItem> candidates,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var sizeGroups = candidates.GroupBy(item => item.FileSize).Where(group => group.Count() > 1).ToList();
        var hashCandidates = sizeGroups.SelectMany(group => group).Distinct().ToList();
        var completed = 0;

        await Parallel.ForEachAsync(hashCandidates,
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 2 },
            async (item, token) =>
            {
                item.Sha256 = await HashService.ComputeSha256Async(item.FilePath, token);
                var current = Interlocked.Increment(ref completed);
                progress?.Report($"Kontrolní součty: {current} / {hashCandidates.Count}");
            });

        return sizeGroups
            .SelectMany(group => group.GroupBy(item => item.Sha256))
            .Where(group => group.Key is not null && group.Count() > 1)
            .Select(group => group.ToList())
            .ToList();
    }

    private static IReadOnlyList<VisualComponent> FindVisualGroups(
        IReadOnlyList<MediaItem> candidates,
        CancellationToken cancellationToken)
    {
        var withHash = candidates.Where(item => item.PerceptualHash.HasValue).ToList();
        var index = new BkTree();
        var union = new DisjointSet(withHash.Count);
        var indexByItem = withHash.Select((item, indexValue) => (item, indexValue))
            .ToDictionary(pair => pair.item, pair => pair.indexValue);
        var edges = new List<(MediaItem Left, MediaItem Right, int Distance)>();

        foreach (var item in withHash)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var match in index.Search(item.PerceptualHash!.Value, 10))
            {
                if (!HasCompatibleAspectRatio(item, match.Item))
                {
                    continue;
                }

                union.Union(indexByItem[item], indexByItem[match.Item]);
                edges.Add((item, match.Item, match.Distance));
            }

            index.Add(item);
        }

        return withHash
            .GroupBy(item => union.Find(indexByItem[item]))
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var items = group.ToList();
                var itemSet = items.ToHashSet();
                var maxDistance = edges
                    .Where(edge => itemSet.Contains(edge.Left) && itemSet.Contains(edge.Right))
                    .Select(edge => edge.Distance)
                    .DefaultIfEmpty(10)
                    .Max();
                return new VisualComponent(items, maxDistance);
            })
            .ToList();
    }

    private static DuplicateGroup CreateGroup(
        int number,
        DuplicateConfidence confidence,
        string reason,
        IReadOnlyCollection<MediaItem> matches)
    {
        var id = $"DUP-{number:0000}";
        var items = new ObservableCollection<MediaItem>(matches.OrderBy(item => item.FilePath));
        QualityRanker.SelectBest(items);

        foreach (var item in items)
        {
            if (confidence > item.DuplicateConfidence)
            {
                item.DuplicateConfidence = confidence;
                item.DuplicateGroupId = id;
            }
        }

        return new DuplicateGroup
        {
            Id = id,
            Confidence = confidence,
            Reason = reason,
            Items = items
        };
    }

    private static bool HasCompatibleAspectRatio(MediaItem left, MediaItem right)
    {
        if (left.Width is not > 0 || left.Height is not > 0 || right.Width is not > 0 || right.Height is not > 0)
        {
            return true;
        }

        var leftRatio = (double)Math.Max(left.Width.Value, left.Height.Value) / Math.Min(left.Width.Value, left.Height.Value);
        var rightRatio = (double)Math.Max(right.Width.Value, right.Height.Value) / Math.Min(right.Width.Value, right.Height.Value);
        return Math.Abs(leftRatio - rightRatio) / Math.Max(leftRatio, rightRatio) <= 0.03;
    }

    private static double SizeRatio(IEnumerable<MediaItem> items)
    {
        var sizes = items.Select(item => item.FileSize).Where(size => size > 0).ToArray();
        return sizes.Length == 0 ? 0 : sizes.Min() / (double)sizes.Max();
    }

    private sealed record VisualComponent(IReadOnlyList<MediaItem> Items, int MaxDistance);

    private sealed class BkTree
    {
        private Node? _root;

        public void Add(MediaItem item)
        {
            if (_root is null)
            {
                _root = new Node(item);
                return;
            }

            var current = _root;
            while (true)
            {
                var distance = HashService.HammingDistance(item.PerceptualHash!.Value, current.Item.PerceptualHash!.Value);
                if (!current.Children.TryGetValue(distance, out var child))
                {
                    current.Children[distance] = new Node(item);
                    return;
                }

                current = child;
            }
        }

        public IEnumerable<(MediaItem Item, int Distance)> Search(ulong hash, int radius)
        {
            if (_root is null)
            {
                yield break;
            }

            var pending = new Stack<Node>();
            pending.Push(_root);
            while (pending.TryPop(out var node))
            {
                var distance = HashService.HammingDistance(hash, node.Item.PerceptualHash!.Value);
                if (distance <= radius)
                {
                    yield return (node.Item, distance);
                }

                foreach (var child in node.Children)
                {
                    if (child.Key >= distance - radius && child.Key <= distance + radius)
                    {
                        pending.Push(child.Value);
                    }
                }
            }
        }

        private sealed class Node(MediaItem item)
        {
            public MediaItem Item { get; } = item;
            public Dictionary<int, Node> Children { get; } = [];
        }
    }

    private sealed class DisjointSet(int count)
    {
        private readonly int[] _parents = Enumerable.Range(0, count).ToArray();
        private readonly byte[] _ranks = new byte[count];

        public int Find(int value)
        {
            while (_parents[value] != value)
            {
                _parents[value] = _parents[_parents[value]];
                value = _parents[value];
            }

            return value;
        }

        public void Union(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot) return;

            if (_ranks[leftRoot] < _ranks[rightRoot])
            {
                _parents[leftRoot] = rightRoot;
            }
            else
            {
                _parents[rightRoot] = leftRoot;
                if (_ranks[leftRoot] == _ranks[rightRoot]) _ranks[leftRoot]++;
            }
        }
    }
}
