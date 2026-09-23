using System.Xml.Linq;
using D2ViewerEditor.Domain.Models;

namespace D2ViewerEditor.Infrastructure.Services.DocumentCompare;

public sealed record XmlDifference(
    DifferenceKind Kind,
    XElement? Left,
    XElement? Right,
    string? Name,
    string? LeftValue,
    string? RightValue);

public sealed record XmlDiffResult(
    IReadOnlyList<XmlDifference> Differences,
    int TotalCount,
    bool Truncated,
    int IgnoredAttributeCount);

public sealed class XmlTreeDiffer
{
    private readonly DocumentCompareOptions _options;

    public XmlTreeDiffer(DocumentCompareOptions options)
    {
        _options = options;
    }

    public XmlDiffResult Compare(
        XElement left,
        XElement right,
        IReadOnlySet<XName> ignoredAttributes,
        IReadOnlySet<XName> ignoredElements,
        int limit,
        CancellationToken cancellationToken)
    {
        var run = new Run(
            new XmlSubtreeHasher(ignoredAttributes, ignoredElements),
            new SequenceAligner(_options.LcsCellLimit, _options.GreedyWindow),
            limit,
            cancellationToken);

        run.CompareElements(left, right);

        return new XmlDiffResult(run.Differences, run.Total, run.Truncated, run.IgnoredAttributes);
    }

    private sealed class Run
    {
        private readonly XmlSubtreeHasher _hasher;
        private readonly SequenceAligner _aligner;
        private readonly int _limit;
        private readonly CancellationToken _cancellationToken;

        public Run(XmlSubtreeHasher hasher, SequenceAligner aligner, int limit, CancellationToken cancellationToken)
        {
            _hasher = hasher;
            _aligner = aligner;
            _limit = limit;
            _cancellationToken = cancellationToken;
        }

        public List<XmlDifference> Differences { get; } = [];
        public int Total { get; private set; }
        public bool Truncated { get; private set; }
        public int IgnoredAttributes => _hasher.IgnoredAttributeCount;

        public void CompareElements(XElement left, XElement right)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (left.Name != right.Name)
            {
                Add(DifferenceKind.ElementNameChanged, left, right, null, Qualified(left), Qualified(right));
                return;
            }

            if (_hasher.Hash(left) == _hasher.Hash(right))
            {
                return;
            }

            CompareAttributes(left, right);

            var leftText = XmlSubtreeHasher.DirectText(left);
            var rightText = XmlSubtreeHasher.DirectText(right);

            if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
            {
                Add(DifferenceKind.TextChanged, left, right, null, leftText, rightText);
            }

            AlignChildren(
                left.Elements().Where(child => !_hasher.IsIgnoredElement(child.Name)).ToList(),
                right.Elements().Where(child => !_hasher.IsIgnoredElement(child.Name)).ToList());
        }

        private void CompareAttributes(XElement left, XElement right)
        {
            var leftAttributes = Attributes(left);
            var rightAttributes = Attributes(right);

            foreach (var (name, leftAttribute) in leftAttributes)
            {
                if (!rightAttributes.TryGetValue(name, out var rightAttribute))
                {
                    Add(DifferenceKind.AttributeOnlyInLeft, left, right, Qualified(left, leftAttribute), leftAttribute.Value, null);
                }
                else if (!string.Equals(leftAttribute.Value, rightAttribute.Value, StringComparison.Ordinal))
                {
                    Add(DifferenceKind.AttributeValueChanged, left, right, Qualified(left, leftAttribute), leftAttribute.Value, rightAttribute.Value);
                }
            }

            foreach (var (name, rightAttribute) in rightAttributes)
            {
                if (!leftAttributes.ContainsKey(name))
                {
                    Add(DifferenceKind.AttributeOnlyInRight, left, right, Qualified(right, rightAttribute), null, rightAttribute.Value);
                }
            }
        }

        private Dictionary<XName, XAttribute> Attributes(XElement element)
        {
            var result = new Dictionary<XName, XAttribute>();

            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration)
                {
                    continue;
                }

                if (_hasher.IsIgnoredAttribute(attribute.Name))
                {
                    continue;
                }

                result[attribute.Name] = attribute;
            }

            return result;
        }

        private void AlignChildren(List<XElement> left, List<XElement> right)
        {
            if (left.Count == 0 && right.Count == 0)
            {
                return;
            }

            foreach (var selector in ElementIdentityKeys.Selectors)
            {
                PairByKey(left, right, selector);
            }

            var leftHashes = left.Select(_hasher.Hash).ToList();
            var rightHashes = right.Select(_hasher.Hash).ToList();
            var anchors = _aligner.Align(leftHashes, rightHashes);
            var anchoredLeft = new HashSet<int>(anchors.Select(anchor => anchor.Left));
            var anchoredRight = new HashSet<int>(anchors.Select(anchor => anchor.Right));

            var movedLeft = new HashSet<int>();
            var movedRight = new HashSet<int>();
            var rightByHash = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);

            for (var j = 0; j < right.Count; j++)
            {
                if (anchoredRight.Contains(j))
                {
                    continue;
                }

                if (!rightByHash.TryGetValue(rightHashes[j], out var queue))
                {
                    queue = new Queue<int>();
                    rightByHash[rightHashes[j]] = queue;
                }

                queue.Enqueue(j);
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (anchoredLeft.Contains(i) || !rightByHash.TryGetValue(leftHashes[i], out var queue) || queue.Count == 0)
                {
                    continue;
                }

                var j = queue.Dequeue();
                movedLeft.Add(i);
                movedRight.Add(j);
                Add(DifferenceKind.ElementMoved, left[i], right[j], Qualified(left[i]), null, null);
            }

            int from = 0, to = 0;

            foreach (var (anchorLeft, anchorRight) in anchors)
            {
                HandleGap(Slice(left, from, anchorLeft, movedLeft), Slice(right, to, anchorRight, movedRight));
                from = anchorLeft + 1;
                to = anchorRight + 1;
            }

            HandleGap(Slice(left, from, left.Count, movedLeft), Slice(right, to, right.Count, movedRight));
        }

        private static List<XElement> Slice(List<XElement> source, int from, int to, HashSet<int> excluded)
        {
            var result = new List<XElement>(Math.Max(0, to - from));

            for (var index = from; index < to; index++)
            {
                if (!excluded.Contains(index))
                {
                    result.Add(source[index]);
                }
            }

            return result;
        }

        private void PairByKey(List<XElement> left, List<XElement> right, Func<XElement, string?> selector)
        {
            var leftKeys = UniqueKeys(left, selector);
            var rightKeys = UniqueKeys(right, selector);

            if (leftKeys.Count == 0 || rightKeys.Count == 0)
            {
                return;
            }

            var pairedLeft = new HashSet<XElement>(ReferenceEqualityComparer.Instance);
            var pairedRight = new HashSet<XElement>(ReferenceEqualityComparer.Instance);

            foreach (var (key, leftElement) in leftKeys)
            {
                if (rightKeys.TryGetValue(key, out var rightElement))
                {
                    CompareElements(leftElement, rightElement);
                    pairedLeft.Add(leftElement);
                    pairedRight.Add(rightElement);
                }
            }

            left.RemoveAll(pairedLeft.Contains);
            right.RemoveAll(pairedRight.Contains);
        }

        private static Dictionary<string, XElement> UniqueKeys(List<XElement> elements, Func<XElement, string?> selector)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var byKey = new Dictionary<string, XElement>(StringComparer.Ordinal);

            foreach (var element in elements)
            {
                var value = selector(element);

                if (value is null)
                {
                    continue;
                }

                var key = $"{element.Name}|{value}";
                counts[key] = counts.GetValueOrDefault(key) + 1;
                byKey[key] = element;
            }

            foreach (var (key, count) in counts)
            {
                if (count > 1)
                {
                    byKey.Remove(key);
                }
            }

            return byKey;
        }

        private void HandleGap(List<XElement> left, List<XElement> right)
        {
            if (left.Count == 0 && right.Count == 0)
            {
                return;
            }

            var pairs = _aligner.Align(
                left.Select(element => element.Name.ToString()).ToList(),
                right.Select(element => element.Name.ToString()).ToList());

            int i = 0, j = 0;

            foreach (var (pairLeft, pairRight) in pairs)
            {
                for (; i < pairLeft; i++) Add(DifferenceKind.ElementOnlyInLeft, left[i], null, Qualified(left[i]), null, null);
                for (; j < pairRight; j++) Add(DifferenceKind.ElementOnlyInRight, null, right[j], Qualified(right[j]), null, null);

                CompareElements(left[pairLeft], right[pairRight]);
                i = pairLeft + 1;
                j = pairRight + 1;
            }

            for (; i < left.Count; i++) Add(DifferenceKind.ElementOnlyInLeft, left[i], null, Qualified(left[i]), null, null);
            for (; j < right.Count; j++) Add(DifferenceKind.ElementOnlyInRight, null, right[j], Qualified(right[j]), null, null);
        }

        private void Add(DifferenceKind kind, XElement? left, XElement? right, string? name, string? leftValue, string? rightValue)
        {
            Total++;

            if (Differences.Count >= _limit)
            {
                Truncated = true;
                return;
            }

            Differences.Add(new XmlDifference(kind, left, right, name, leftValue, rightValue));
        }

        private static string Qualified(XElement element)
        {
            var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : $"{prefix}:{element.Name.LocalName}";
        }

        private static string Qualified(XElement scope, XAttribute attribute)
        {
            if (attribute.Name.Namespace == XNamespace.None)
            {
                return attribute.Name.LocalName;
            }

            var prefix = scope.GetPrefixOfNamespace(attribute.Name.Namespace);
            return string.IsNullOrEmpty(prefix) ? $"{{{attribute.Name.NamespaceName}}}{attribute.Name.LocalName}" : $"{prefix}:{attribute.Name.LocalName}";
        }
    }
}
