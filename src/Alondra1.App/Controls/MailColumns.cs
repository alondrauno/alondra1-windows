using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Alondra1.Core.Mail;

namespace Alondra1.App.Controls;

/// <summary>
/// The one column layout of the message list table, shared by the header (From / Account / Subject / Received) and
/// every message row, so their columns stay aligned at any width and move together when a column edge is dragged.
/// Set <c>ui:MailColumns.Apply="True"</c> on a Grid. Columns: 0 unread and flag, 1 From, 2 Account (unified views),
/// 3 Subject (the rest of the width), 4 attachment, 5 Received.
/// </summary>
public static class MailColumns
{
    public const int Indicator = 0, From = 1, Account = 2, Subject = 3, Attachment = 4, Received = 5;

    public static readonly DependencyProperty ApplyProperty = DependencyProperty.RegisterAttached(
        "Apply", typeof(bool), typeof(MailColumns), new PropertyMetadata(false, OnApplyChanged));

    public static bool GetApply(DependencyObject element) => (bool)element.GetValue(ApplyProperty);

    public static void SetApply(DependencyObject element, bool value) => element.SetValue(ApplyProperty, value);

    /// <summary>The current widths; the window updates it from the view model's resolved layout.</summary>
    public static MailColumnsState Current { get; } = new();

    private static void OnApplyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Grid grid || e.NewValue is not true)
            return;
        grid.ColumnDefinitions.Clear();
        foreach (var name in new[] { nameof(MailColumnsState.Indicator), nameof(MailColumnsState.From), nameof(MailColumnsState.Account),
                     nameof(MailColumnsState.Subject), nameof(MailColumnsState.Attachment), nameof(MailColumnsState.Received) })
        {
            var column = new ColumnDefinition();
            BindingOperations.SetBinding(column, ColumnDefinition.WidthProperty, new Binding(name) { Source = Current, Mode = BindingMode.OneWay });
            grid.ColumnDefinitions.Add(column);
        }
    }
}

public sealed class MailColumnsState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public GridLength Indicator { get; private set; } = new(MailColumnLayout.IndicatorWidth);
    public GridLength From { get; private set; } = new(1, GridUnitType.Star);
    public GridLength Account { get; private set; } = new(0);
    /// <summary>Always the remaining width, so rows fill the list with or without its scroll bar.</summary>
    public GridLength Subject { get; } = new(1, GridUnitType.Star);
    public GridLength Attachment { get; private set; } = new(MailColumnLayout.AttachmentWidth);
    public GridLength Received { get; private set; } = new(MailColumnLayout.DefaultReceived);

    private MailColumnWidths? _applied;

    public void Apply(MailColumnWidths widths)
    {
        if (widths == _applied)
            return;
        _applied = widths;
        Indicator = new GridLength(widths.Indicator);
        From = new GridLength(widths.From);
        Account = new GridLength(widths.Account);
        Attachment = new GridLength(widths.Attachment);
        Received = new GridLength(widths.Received);
        foreach (var name in new[] { nameof(Indicator), nameof(From), nameof(Account), nameof(Attachment), nameof(Received) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
