using System.Drawing;
using Xunit;

namespace Sessionwright.Tests;

// The still picture Windows shows for a covered session on the taskbar: small, so what is on it has to be placed for it.
public class CoverPictureTests
{
    static int Painted(Bitmap picture, int fromRow, int toRow)
    {
        var stage = Cover.Stage.ToArgb();
        var count = 0;
        for (var y = fromRow; y < toRow; y++)
            for (var x = 0; x < picture.Width; x++)
                if (picture.GetPixel(x, y).ToArgb() != stage) count++;
        return count;
    }

    // The words sit in the middle; the ring, when something runs, above them. Without a ring the top stays empty.
    [Fact]
    public void ATaskbarSizedPictureHasItsWordsAndTheRingOnlyWhenAsked()
    {
        using var question = Cover.Picture(200, 108, "SRV01", "Sign out?");
        using var signingOut = Cover.Picture(200, 108, "Signing out…", "SRV01", ring: true);

        Assert.True(Painted(question, 36, 80) > 200, "no words in the middle");
        Assert.Equal(0, Painted(question, 0, 28));
        Assert.True(Painted(signingOut, 4, 34) > 30, "no ring above the words");
        Assert.True(Painted(signingOut, 40, 96) > 200, "no words under the ring");
        // Nothing runs off the picture.
        Assert.Equal(0, Painted(signingOut, 0, 2) + Painted(signingOut, 106, 108));

        // For looking at it while working on the drawing: SESSIONWRIGHT_TEST_PICTURES=<folder>.
        if (Environment.GetEnvironmentVariable("SESSIONWRIGHT_TEST_PICTURES") is { Length: > 0 } folder)
        {
            question.Save(System.IO.Path.Combine(folder, "taskbar-question.png"));
            signingOut.Save(System.IO.Path.Combine(folder, "taskbar-signing-out.png"));
        }
    }
}
