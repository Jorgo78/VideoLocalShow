using VideoLocalShow.Models;

namespace VideoLocalShow;

/// <summary>Picks the group-heading look or the video-row look for each item of the downloads list.</summary>
public class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? FileTemplate { get; set; }

    public DataTemplate? HeaderTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        (item is GroupHeader ? HeaderTemplate : FileTemplate)!;
}
