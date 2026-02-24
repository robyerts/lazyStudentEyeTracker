using OpenCvSharp;

Console.WriteLine("Probing camera indices 0-5...\n");

for (int i = 0; i < 5; i++)
{
    var found = false;
    var thread = new Thread(() =>
    {
        try
        {
            using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
            if (cap.IsOpened())
            {
                var w = cap.Get(VideoCaptureProperties.FrameWidth);
                var h = cap.Get(VideoCaptureProperties.FrameHeight);
                Console.WriteLine($"  Camera {i}: AVAILABLE ({w}x{h}) [DirectShow]");
                found = true;
                cap.Release();
            }
            else
            {
                Console.WriteLine($"  Camera {i}: not available");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Camera {i}: error - {ex.Message}");
        }
    });
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(3)))
    {
        Console.WriteLine($"  Camera {i}: timed out (likely not available)");
    }
}

Console.WriteLine("\nDone.");

