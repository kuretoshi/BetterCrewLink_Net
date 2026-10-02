using System.Windows;
using System.Windows.Controls;

namespace TanukiBCL.Client;

// Keeps the translation key when a language change replaces the original XAML
// literal, so switching languages repeatedly does not depend on the old text.
internal sealed class LocalizedStaticText
{
    private readonly Dictionary<DependencyObject, string> keys = [];

    internal void Apply(DependencyObject root, string language, DependencyObject? excluded = null)
    {
        Visit(root);
        void Visit(DependencyObject element)
        {
            if (ReferenceEquals(element, excluded)) return;
            if (element is TextBlock text)
            {
                var key = Resolve(element, text.Text);
                if (key is not null) text.Text = UiLocalization.Translate(language, key);
            }
            else if (element is ContentControl { Content: string literal } control)
            {
                var key = Resolve(element, literal);
                if (key is not null) control.Content = UiLocalization.Translate(language, key);
            }
            foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
                Visit(child);
        }
    }

    private string? Resolve(DependencyObject element, string literal)
    {
        if (keys.TryGetValue(element, out var known)) return known;
        var key = UiLocalization.KeyForJapanese(literal);
        if (key is not null) keys.Add(element, key);
        return key;
    }
}
