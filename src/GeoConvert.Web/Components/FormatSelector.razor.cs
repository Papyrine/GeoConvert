namespace GeoConvert.Web.Components;

public partial class FormatSelector
{
    [Parameter]
    public string Label { get; set; } = "Format";

    [Parameter]
    public IReadOnlyList<FormatInfo> Formats { get; set; } = [];

    [Parameter]
    public GeoFormat Selected { get; set; }

    [Parameter]
    public EventCallback<GeoFormat> SelectedChanged { get; set; }

    Task OnChanged(ChangeEventArgs args)
    {
        var format = Enum.Parse<GeoFormat>((string) args.Value!);
        return SelectedChanged.InvokeAsync(format);
    }
}
