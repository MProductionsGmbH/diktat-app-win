using System.Windows.Markup;

namespace Diktat.Windows.Views;

/// A text of the interface in XAML: Text="{l:T 'Deutscher Text'}" shows the English version
/// when the app runs in English. The language is fixed for a run, so the text is read once
/// when the element loads. xmlns:l="clr-namespace:Diktat.Windows.Views".
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
