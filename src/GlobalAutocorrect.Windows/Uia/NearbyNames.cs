using System.Runtime.InteropServices;
using GlobalAutocorrect.Core.Instant;
using Interop.UIAutomationClient;

namespace GlobalAutocorrect.Windows.Uia;

/// <summary>
/// D92, user request (Intercom: "Reply Christopher Moyer" above a reply that began "Hi Chrisopher,"): names the app
/// shows around the field being typed in. The labels near the field (not the field itself) are read once per field and
/// kept in memory only: never logged or saved (8.6).
/// </summary>
internal sealed class NearbyNames
{
    private const int NamePropertyId = 30005, BoundingRectanglePropertyId = 30001, ControlTypePropertyId = 30003;
    private const int TextControlTypeId = 50020;
    /// <summary>Up this many containers from the field, while the container holds at most <see cref="MaxLabels"/> labels.</summary>
    private const int MaxLevels = 6, MaxLabels = 150;

    private string _for = "";
    private IReadOnlyList<string> _names = [];

    public IReadOnlyList<string> For(IUIAutomation2 uia, IUIAutomationElement field, string runtimeId)
    {
        if (runtimeId == _for) return _names;
        _for = runtimeId;
        try { _names = Read(uia, field); }
        catch (Exception e) when (e is COMException or InvalidCastException or TimeoutException) { _names = []; }
        return _names;
    }

    private static IReadOnlyList<string> Read(IUIAutomation2 uia, IUIAutomationElement field)
    {
        var fieldRect = field.CurrentBoundingRectangle;
        var cache = uia.CreateCacheRequest();
        cache.AddProperty(NamePropertyId);
        cache.AddProperty(BoundingRectanglePropertyId);
        var isText = uia.CreatePropertyCondition(ControlTypePropertyId, TextControlTypeId);
        var walker = uia.ControlViewWalker;

        List<string> labels = [];
        var at = field;
        for (int level = 0; level < MaxLevels; level++)
        {
            at = walker.GetParentElement(at);
            if (at is null) break;
            var found = at.FindAllBuildCache(TreeScope.TreeScope_Descendants, isText, cache);
            if (found is null || found.Length > MaxLabels) break;   // the whole page: too far from the field
            labels.Clear();
            for (int i = 0; i < found.Length; i++)
            {
                var e = found.GetElement(i);
                if (Within(e.CachedBoundingRectangle, fieldRect)) continue;   // the field's own text is what's being typed
                if (e.CachedName is { Length: > 0 } n) labels.Add(n);
            }
        }
        return NameFix.Candidates(labels);
    }

    private static bool Within(tagRECT r, tagRECT field)
    {
        int cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
        return cx >= field.left && cx <= field.right && cy >= field.top && cy <= field.bottom;
    }
}
