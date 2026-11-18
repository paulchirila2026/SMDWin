using SMDWin.Services;
using SMDWin.Views;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The Benchmark drawing picks one of four computers from the detected kind.</summary>
    public class DeviceSketchKindTests
    {
        [Theory]
        [InlineData(ComputerKind.Laptop, DeviceSketch.Kind.Laptop)]
        [InlineData(ComputerKind.Ultrabook, DeviceSketch.Kind.Laptop)]
        [InlineData(ComputerKind.Tablet, DeviceSketch.Kind.Laptop)]
        [InlineData(ComputerKind.AllInOne, DeviceSketch.Kind.AllInOne)]
        [InlineData(ComputerKind.MiniPc, DeviceSketch.Kind.MiniPc)]
        [InlineData(ComputerKind.Tower, DeviceSketch.Kind.Desktop)]
        [InlineData(ComputerKind.SmallDesktop, DeviceSketch.Kind.Desktop)]
        [InlineData(ComputerKind.Server, DeviceSketch.Kind.Desktop)]
        public void MapsComputerKind(ComputerKind kind, DeviceSketch.Kind expected)
            => Assert.Equal(expected, DeviceSketch.KindFor(kind));
    }
}
