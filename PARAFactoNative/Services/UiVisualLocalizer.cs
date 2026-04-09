using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PARAFactoNative.Services;

public static class UiVisualLocalizer
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, string>> Originals = new();

    public static void Localize(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>();
        Walk(root, seen);
    }

    private static void Walk(DependencyObject node, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(node)) return;
        LocalizeNode(node);

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d)
                Walk(d, seen);
        }

        if (node is Visual or Visual3D)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Walk(VisualTreeHelper.GetChild(node, i), seen);
        }
    }

    private static void LocalizeNode(DependencyObject node)
    {
        switch (node)
        {
            case Window w:
                LocalizeWindow(w);
                break;
            case TextBlock tb:
                if (!BindingOperations.IsDataBound(tb, TextBlock.TextProperty))
                    tb.Text = TranslateFor(tb, "Text", tb.Text);
                break;
            case Run run:
                run.Text = TranslateFor(run, "Text", run.Text);
                break;
            case Button b:
                if (b.Content is string bs && !BindingOperations.IsDataBound(b, ContentControl.ContentProperty))
                    b.Content = TranslateFor(b, "Content", bs);
                break;
            case CheckBox cb:
                if (cb.Content is string cbs && !BindingOperations.IsDataBound(cb, ContentControl.ContentProperty))
                    cb.Content = TranslateFor(cb, "Content", cbs);
                break;
            case RadioButton rb:
                if (rb.Content is string rbs && !BindingOperations.IsDataBound(rb, ContentControl.ContentProperty))
                    rb.Content = TranslateFor(rb, "Content", rbs);
                break;
            case Label lb:
                if (lb.Content is string ls && !BindingOperations.IsDataBound(lb, ContentControl.ContentProperty))
                    lb.Content = TranslateFor(lb, "Content", ls);
                break;
            case GroupBox gb:
                if (gb.Header is string hs && !BindingOperations.IsDataBound(gb, HeaderedContentControl.HeaderProperty))
                    gb.Header = TranslateFor(gb, "Header", hs);
                break;
            case TabItem ti:
                if (ti.Header is string ths && !BindingOperations.IsDataBound(ti, HeaderedContentControl.HeaderProperty))
                    ti.Header = TranslateFor(ti, "Header", ths);
                break;
            case DataGrid dg:
                LocalizeDataGridHeaders(dg);
                break;
            case FrameworkElement fe:
                if (fe.ToolTip is string tts)
                    fe.ToolTip = TranslateFor(fe, "ToolTip", tts);
                break;
        }
    }

    private static void LocalizeWindow(Window w)
    {
        if (!BindingOperations.IsDataBound(w, Window.TitleProperty))
            w.Title = TranslateFor(w, "Title", w.Title);
    }

    private static void LocalizeDataGridHeaders(DataGrid dg)
    {
        foreach (var c in dg.Columns)
        {
            if (c.Header is string hs)
                c.Header = TranslateFor(c, "Header", hs);
        }
    }

    private static string TranslateFor(object node, string key, string current)
    {
        var originals = Originals.GetOrCreateValue(node);
        if (!originals.TryGetValue(key, out var original))
        {
            original = current;
            originals[key] = original;
        }
        return UiTextTranslator.Translate(original);
    }
}
