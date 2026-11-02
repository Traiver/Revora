using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Revora;

public sealed record ReviewItem(string Name, string Detail, string TechnicalDetails = "", string TechnicalLabel = "Technische Details anzeigen")
{
    public Visibility TechnicalVisibility => TechnicalDetails.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
public static class Ui
{
    public static Window Load(string file)
    {
        var uri = new Uri("pack://application:,,,/Revora;component/Views/" + file, UriKind.Absolute);
        var info = Application.GetResourceStream(uri) ?? throw new InvalidOperationException("Oberfläche nicht gefunden: " + file);
        using var stream = info.Stream;
        var window = (Window)XamlReader.Load(stream, new ParserContext { BaseUri = uri });
        window.Icon = (ImageSource)window.FindResource("RevoraIcon");
        if (file == "MainWindow.xaml")
        {
            var scroll = window.C<ScrollViewer>("MainScroll");
            // The list has no independent viewport. Handle the preview event before
            // its internal ScrollViewer consumes the wheel, including over row buttons.
            window.C<ListBox>("ProgramsList").PreviewMouseWheel += (_, e) =>
            {
                int lines = SystemParameters.WheelScrollLines;
                double distance = lines < 0 ? scroll.ViewportHeight : lines * 16.0;
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120.0 * distance);
                e.Handled = true;
            };
        }
        return window;
    }
    public static T C<T>(this Window window, string name) where T : class => window.FindName(name) as T ?? throw new InvalidOperationException("Bedienelement fehlt: " + name);
    public static void Owner(Window dialog, Window owner) { if (owner.IsLoaded) dialog.Owner = owner; }
    public static void Message(Window owner, string summary, string details = "", string title = "Hinweis")
    {
        var dialog = Review(owner, title, "", new[] { new ReviewItem("", summary, details) }, "Schließen");
        dialog.C<Button>("CancelReview").Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }
    public static void RouteRowButtons(Window window, Action<Button> action)
    {
        window.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null && node is not Button)
            {
                try { node = VisualTreeHelper.GetParent(node); }
                catch (InvalidOperationException) { node = LogicalTreeHelper.GetParent(node); }
            }
            if (node is Button button && (button.Name == "OpenInstallFolderButton" || button.Name == "ChangeSourceButton" || button.Name == "MuteUpdatesButton")) { action(button); e.Handled = true; }
        }), true);
    }
    public static Window Review(Window owner, string title, string intro, IEnumerable<ReviewItem> items, string button, string ack = "")
    {
        var dialog = Load("ReviewWindow.xaml"); Owner(dialog, owner); dialog.Title = title;
        dialog.C<TextBlock>("ReviewTitle").Text = title; dialog.C<TextBlock>("ReviewIntro").Text = intro;
        dialog.C<ItemsControl>("ReviewItems").ItemsSource = items.ToArray();
        var accept = dialog.C<Button>("ConfirmReview"); accept.Content = button;
        var box = dialog.C<CheckBox>("AckBox");
        if (ack.Length == 0) box.Visibility = Visibility.Collapsed;
        else dialog.C<TextBlock>("AckText").Text = ack;
        accept.IsEnabled = ack.Length == 0;
        box.Checked += (_, _) => accept.IsEnabled = true; box.Unchecked += (_, _) => accept.IsEnabled = false;
        accept.Click += (_, _) => dialog.DialogResult = true; dialog.C<Button>("CancelReview").Click += (_, _) => dialog.DialogResult = false;
        return dialog;
    }
    public static string Prompt(Window owner, string title, string message, string initial)
    {
        var dialog = new Window { Title = title, Width = 500, Height = 270, MinWidth = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(16,30,45)), Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI") };
        Owner(dialog, owner); var panel = new StackPanel { Margin = new Thickness(24) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var input = new TextBox { Text = initial, Margin = new Thickness(0,16,0,16), Padding = new Thickness(8) }; panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,10,0) };
        var save = new Button { Content = "Übernehmen", Padding = new Thickness(12,8,12,8) }; buttons.Children.Add(cancel); buttons.Children.Add(save);
        cancel.Click += (_, _) => dialog.DialogResult = false; save.Click += (_, _) => dialog.DialogResult = true;
        return dialog.ShowDialog() == true ? input.Text.Trim() : "";
    }
}
