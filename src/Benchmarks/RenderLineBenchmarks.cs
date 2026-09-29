[MemoryDiagnoser]
public class RenderLineBenchmarks
{
    FeatureCollection data = null!;
    RenderOptions options = null!;

    [GlobalSetup]
    public void Setup()
    {
        data = SampleData.LongLines(50);
        options = new()
        {
            Width = 1024,
            Height = 768,
            Png = new()
            {
                Compression = CompressionLevel.NoCompression
            }
        };
    }

    [Benchmark]
    public int Full_NoCompression() =>
        MapRenderer.RenderPng(data, options).Length;
}