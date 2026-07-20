using System.Collections;
using System.Collections.Generic;

namespace AurevonUI;

public enum Orientation { Vertical, Horizontal }

public sealed class ItemsControl
{
    private readonly AuiWindow _owner;

    internal ItemsControl(AuiWindow owner, string templateId, Control container, Orientation orientation, float spacing)
    {
        _owner = owner;
        TemplateId = templateId;
        Container = container;
        Orientation = orientation;
        Spacing = spacing;
    }

    public string TemplateId { get; }

    public Control Container { get; }

    public Orientation Orientation { get; }

    public float Spacing { get; set; }

    public System.Action<Control, object>? OnItemCreated { get; set; }

    public ItemsControl Bind<[global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties | global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicFields)] T>(System.Collections.Generic.IEnumerable<T> items, System.Action<Control, T>? onItem = null)
    {
        if (onItem is not null)
            OnItemCreated = (v, d) => onItem(v, (T)d);
        ItemsSource = (IEnumerable)items;
        return this;
    }

    public IReadOnlyList<Control> Items => _generated;
    internal readonly List<Control> _generated = new();

    private IEnumerable? _itemsSource;

    public IEnumerable? ItemsSource
    {
        get => _itemsSource;
        set
        {
            _itemsSource = value;
            _owner.GenerateItems(this, value);
        }
    }
}
