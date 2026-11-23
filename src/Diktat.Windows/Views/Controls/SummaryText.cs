using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Diktat.AI.Meetings;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Diktat.Windows.Views.Controls;

/// A summary or live notes on a card (SummaryText on the Mac): headings stand out, bullet points
/// get a "•", and the Markdown marks a model adds on its own are gone. Read-only, selectable.
public sealed class SummaryText : RichTextBox
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SummaryText), new PropertyMetadata("", (d, _) => ((SummaryText)d).Rebuild()));

    /// The title of the card; a first heading saying the same is left out.
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SummaryText), new PropertyMetadata(null, (d, _) => ((SummaryText)d).Rebuild()));

    public SummaryText()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsTabStop = false;
        Background = System.Windows.Media.Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        FontSize = 13.5;
        SetResourceReference(ForegroundProperty, "Diktat.Text");
        SetResourceReference(SelectionBrushProperty, "Diktat.Accent");
        SetResourceReference(FocusVisualStyleProperty, "Diktat.NoFocusVisual");
        // Like Diktat.ReadOnlyText: no border, no scroll bars; the page scrolls.
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        Template = new ControlTemplate(typeof(RichTextBox)) { VisualTree = host };
        Rebuild();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private void Rebuild()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var lines = ListDisplay.Lines(Text ?? "", Title);
        for (var i = 0; i < lines.Count; i++)
        {
            paragraph.Inlines.Add(new Run(lines[i].Text) { FontWeight = lines[i].IsHeading ? FontWeights.SemiBold : FontWeights.Normal });
            if (i < lines.Count - 1) paragraph.Inlines.Add(new LineBreak());
        }
        Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }
}
