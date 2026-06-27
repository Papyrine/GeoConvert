namespace GeoConvert.App;

/// <summary>Builds value-carrying <see cref="ComboBox"/>es (label shown, typed value behind it).</summary>
static class Combos
{
    public static ComboBox Build<T>(IReadOnlyList<(T Value, string Label)> choices, T current, Action<T> onChange)
        where T : notnull
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 170,
            Margin = new(3),
        };
        foreach (var (value, label) in choices)
        {
            combo.Items.Add(new Choice<T>(value, label));
        }

        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(((Choice<T>) combo.Items[index]!).Value, current))
            {
                combo.SelectedIndex = index;
                break;
            }
        }

        combo.SelectedIndexChanged += (_, _) => onChange(((Choice<T>) combo.SelectedItem!).Value);
        return combo;
    }

    /// <summary>Selects the item carrying <paramref name="value"/>, raising the change handler as a user pick would.</summary>
    public static void Select<T>(ComboBox combo, T value)
        where T : notnull
    {
        for (var index = 0; index < combo.Items.Count; index++)
        {
            if (combo.Items[index] is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                combo.SelectedIndex = index;
                return;
            }
        }
    }

    sealed class Choice<T>(T value, string label)
    {
        public T Value { get; } = value;

        public override string ToString() => label;
    }
}
