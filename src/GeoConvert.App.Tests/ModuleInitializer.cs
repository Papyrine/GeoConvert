public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        // Verify.WinForms snapshots WinForms UI as images. Its Control converter renders via a live
        // message loop, which deadlocks headless, so the tests render each control to a Bitmap on an STA
        // thread (see WinFormsSnapshot) and snapshot that. This converter writes a Bitmap out as a PNG so
        // Verify treats it as an image; SSIM comparison then tolerates the small machine-dependent pixel
        // differences inherent in rendering WinForms to a bitmap.
        VerifyWinForms.Initialize();
        VerifierSettings.UseSsimForPng();
        VerifierSettings.RegisterFileConverter<Bitmap>(
            (bitmap, _) =>
            {
                var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                return new(null, "png", stream, null);
            });
    }
}
