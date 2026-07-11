// Drives the GeoParquet reader's defensive branches by hand-assembling minimal (but correctly framed)
// Parquet files via the internal helpers — giving exact control over page type, encoding, codec and
// physical type, which is awkward to coax out of a real Parquet writer.
public class GeoParquetCraftTests
{
    const string goodGeo =
        """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"WKB"}}}""";

    const string nonWkbGeo =
        """{"version":"1.1.0","primary_column":"geometry","columns":{"geometry":{"encoding":"point"}}}""";

    [Test]
    [Arguments("encoding")]
    [Arguments("dictionary")]
    [Arguments("codec")]
    [Arguments("type")]
    [Arguments("nogeo")]
    [Arguments("nonwkb")]
    public async Task Rejects(string kind)
    {
        var data = kind switch
        {
            "encoding" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingRle,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo),
            "dictionary" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingRleDictionary,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo),
            "codec" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                4 /* Brotli */, ParquetMetadata.TypeByteArray, goodGeo),
            "type" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                ParquetMetadata.CodecUncompressed, 4 /* Float */, goodGeo),
            "nogeo" => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, geo: null),
            _ => Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
                ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, nonWkbGeo),
        };

        using var stream = new MemoryStream(data);
        await Assert.That(TestSupport.ThrowsGeo(() => GeoParquet.Read(stream))).IsTrue();
    }

    // A count read off the footer or a page header used to presize a buffer with no cross-check against
    // the file: NumRows presizes an object?[] per column, TotalCompressedSize presizes the chunk buffer,
    // and a page's NumValues presizes the definition-level and value arrays. See AllocationBoundsTests for
    // the same class of bug in the DBF, WKB and FlatGeobuf readers. GeoParquet.Read wraps *any* exception
    // as GeoConvertException, so these assert on the message — the type alone passed before the bound
    // existed, via the catch-all swallowing the presize's ArgumentOutOfRange/OutOfMemoryException.
    [Test]
    public async Task Row_count_is_bounded_by_the_file()
    {
        var data = Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
            ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo, groupRows: 1_000_000);

        using var stream = new MemoryStream(data);
        await Assert.That(TestSupport.GeoMessage(() => GeoParquet.Read(stream)))
            .Contains("declares 1000000 rows");
    }

    [Test]
    public async Task Negative_row_count_is_rejected()
    {
        // `(int)-1` rows reached `new object?[-1]`, an OverflowException the catch-all restated as a
        // "Invalid GeoParquet data" message that named neither the field nor the value.
        var data = Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
            ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo, groupRows: -1);

        using var stream = new MemoryStream(data);
        await Assert.That(TestSupport.GeoMessage(() => GeoParquet.Read(stream)))
            .Contains("declares -1 rows");
    }

    [Test]
    [Arguments(1_000_000_000L, "Parquet read of 1000000000 bytes")]
    [Arguments(-1L, "Parquet read of -1 bytes")]
    [Arguments(long.MaxValue, "Parquet read of 9223372036854775807 bytes")]
    public async Task Column_chunk_size_is_bounded_by_the_file(long compressedSize, string expected)
    {
        // Past the end of the file, negative, and past Array.MaxLength — all three presized `buffer`.
        var data = Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
            ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo,
            compressedSize: compressedSize);

        using var stream = new MemoryStream(data);
        await Assert.That(TestSupport.GeoMessage(() => GeoParquet.Read(stream))).Contains(expected);
    }

    [Test]
    public async Task Negative_page_value_count_is_rejected()
    {
        var data = Craft(ParquetMetadata.PageData, ParquetMetadata.EncodingPlain,
            ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo, pageValues: -1);

        using var stream = new MemoryStream(data);
        await Assert.That(TestSupport.GeoMessage(() => GeoParquet.Read(stream)))
            .Contains("declares -1 values");
    }

    // A data page cannot hold more values than the row group has rows left to fill; a dictionary page
    // cannot hold more than eight per decompressed byte (the one-bit BOOLEAN floor).
    [Test]
    [Arguments(ParquetMetadata.PageData)]
    [Arguments(ParquetMetadata.PageDictionary)]
    public async Task Page_value_count_is_bounded(int pageType)
    {
        var data = Craft(pageType, ParquetMetadata.EncodingPlain,
            ParquetMetadata.CodecUncompressed, ParquetMetadata.TypeByteArray, goodGeo, pageValues: 1_000_000);

        using var stream = new MemoryStream(data);
        await Assert.That(TestSupport.GeoMessage(() => GeoParquet.Read(stream)))
            .Contains("declares 1000000 values");
    }

    static byte[] Craft(
        int pageType,
        int encoding,
        int codec,
        int columnType,
        string? geo,
        long groupRows = 1,
        int pageValues = 1,
        long? compressedSize = null)
    {
        var definitionBytes = ParquetEncoding.EncodeRle([1], 1);
        using var body = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, definitionBytes.Length);
        body.Write(length);
        body.Write(definitionBytes);
        var bodyBytes = body.ToArray();

        using var memory = new MemoryStream();
        memory.Write("PAR1"u8);
        var header = ParquetMetadata.WritePageHeader(new()
        {
            Type = pageType,
            UncompressedSize = bodyBytes.Length,
            CompressedSize = bodyBytes.Length,
            NumValues = pageValues,
            Encoding = encoding,
        });
        var dataPageOffset = (int)memory.Position;
        memory.Write(header);
        memory.Write(bodyBytes);

        var file = new ParquetMetadata.File
        {
            NumRows = groupRows,
            CreatedBy = "test",
            Schema =
            [
                new()
                {
                    Name = "schema",
                    NumChildren = 1
                },
                new()
                {
                    Name = "geometry",
                    Type = columnType,
                    Repetition = ParquetMetadata.RepetitionOptional,
                },
            ],
            KeyValueMetadata = geo == null ? [] : [("geo", geo)],
            RowGroups =
            [
                new()
                {
                    NumRows = groupRows,
                    TotalByteSize = bodyBytes.Length,
                    Columns =
                    [
                        new()
                        {
                            Type = columnType,
                            Codec = codec,
                            Encodings = [encoding],
                            Path = ["geometry"],
                            NumValues = pageValues,
                            TotalUncompressedSize = header.Length + bodyBytes.Length,
                            TotalCompressedSize = compressedSize ?? header.Length + bodyBytes.Length,
                            DataPageOffset = dataPageOffset,
                        },
                    ],
                },
            ],
        };

        var footer = ParquetMetadata.WriteFile(file);
        memory.Write(footer);
        Span<byte> footerLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(footerLength, footer.Length);
        memory.Write(footerLength);
        memory.Write("PAR1"u8);
        return memory.ToArray();
    }
}
