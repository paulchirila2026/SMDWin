using System.Collections.Generic;
using SMDWin.Models;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The channel mode is decided only from the channels written by the firmware, not from the slot positions.
    public class RamChannelTests
    {
        private static RamEntry Mod(string slot, string bank, ulong gb = 4) =>
            new() { SlotLabel = slot, BankLabel = bank, CapacityBytes = gb << 30 };
        private static RamEntry Empty() => new() { IsEmpty = true };

        [Theory]
        [InlineData("ChannelA-DIMM0", "BANK 0", "A")]
        [InlineData("DIMM_B1", "", "B")]
        [InlineData("A2_DIMM", "", "A")]
        [InlineData("DIMM 1", "P0 CHANNEL B", "B")]
        [InlineData("P0_Node0_Channel1_Dimm0", "", "1")]
        [InlineData("DIMM0 #1", "BANK 0", null)]
        [InlineData("Slot 3", "BANK 2", null)]
        public void ChannelOf_ReadsOnlyExplicitChannels(string slot, string bank, string? expected) =>
            Assert.Equal(expected, RamDiagnosticsService.ChannelOf(Mod(slot, bank)));

        [Fact]
        public void BankLabelsWithoutChannels_AreUnknown_NotSingle()
        {
            // iMac14,2: "DIMM0 #1 / BANK 0" and "DIMM0 #2 / BANK 1" in slots 1 and 4
            var mods = new List<RamEntry> { Mod("DIMM0 #1", "BANK 0"), Empty(), Empty(), Mod("DIMM0 #2", "BANK 1") };
            var r = RamDiagnosticsService.DetectChannelMode(mods);
            Assert.Equal(DualChannelStatus.Unknown, r.Status);
            Assert.Equal("", r.Advice);
        }

        [Fact]
        public void TwoChannels_AreDual()
        {
            var mods = new List<RamEntry> { Mod("ChannelA-DIMM0", "BANK 0"), Empty(), Mod("ChannelB-DIMM0", "BANK 2"), Empty() };
            Assert.Equal(DualChannelStatus.DualChannel, RamDiagnosticsService.DetectChannelMode(mods).Status);
        }

        [Fact]
        public void SameChannel_IsSingle_WithAdvice()
        {
            var mods = new List<RamEntry> { Mod("ChannelA-DIMM0", ""), Mod("ChannelA-DIMM1", ""), Empty(), Empty() };
            var r = RamDiagnosticsService.DetectChannelMode(mods);
            Assert.Equal(DualChannelStatus.SingleChannel, r.Status);
            Assert.NotEqual("", r.Advice);
        }

        [Fact]
        public void FourModulesOnTwoChannels_AreDual_NotQuad()
        {
            var mods = new List<RamEntry> { Mod("DIMM_A1", ""), Mod("DIMM_A2", ""), Mod("DIMM_B1", ""), Mod("DIMM_B2", "") };
            Assert.Equal(DualChannelStatus.DualChannel, RamDiagnosticsService.DetectChannelMode(mods).Status);
        }

        [Fact]
        public void UnequalChannels_AreFlex()
        {
            var mods = new List<RamEntry> { Mod("DIMM_A1", "", 8), Mod("DIMM_B1", "", 4) };
            Assert.Equal(DualChannelStatus.FlexMode, RamDiagnosticsService.DetectChannelMode(mods).Status);
        }

        [Fact]
        public void OneModule_IsSingle()
        {
            var mods = new List<RamEntry> { Mod("DIMM_A1", ""), Empty() };
            Assert.Equal(DualChannelStatus.SingleChannel, RamDiagnosticsService.DetectChannelMode(mods).Status);
        }
    }
}
