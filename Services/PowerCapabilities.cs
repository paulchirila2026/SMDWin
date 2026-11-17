using System;

namespace SMDWin.Services
{
    /// <summary>
    /// Which sleep states the system has, read from the Windows function GetPwrCapabilities (SYSTEM_POWER_CAPABILITIES
    /// structure). Unlike the text shown by "powercfg /a", the answer does not depend on the Windows language and needs no
    /// process.
    /// </summary>
    public readonly record struct PowerCapabilities(bool Sleep, bool Hibernate, bool ModernStandby)
    {
        // field positions in SYSTEM_POWER_CAPABILITIES (each is one byte: 0 = no, anything else = yes)
        private const int SystemS3 = 5, SystemS4 = 6, HiberFilePresent = 8, AoAc = 20;

        /// <summary>How many bytes the answer must have to be read.</summary>
        public const int MinBytes = AoAc + 1;

        /// <summary>Size of the buffer given to the function (the structure has 76 bytes; spare room for future versions).</summary>
        public const int BufferBytes = 128;

        /// <summary>null when the answer is too short to be read.</summary>
        public static PowerCapabilities? Parse(ReadOnlySpan<byte> caps)
        {
            if (caps.Length < MinBytes) return null;
            return new PowerCapabilities(
                Sleep: caps[SystemS3] != 0,
                // hibernation exists only if the board supports it AND the hibernation file is enabled
                Hibernate: caps[SystemS4] != 0 && caps[HiberFilePresent] != 0,
                ModernStandby: caps[AoAc] != 0);
        }
    }
}
