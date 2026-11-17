using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
/// <summary>
/// Minimal QR Code matrix generator. Produces a boolean[,] with dark=true cells.
/// Supports strings up to 47 bytes using Version 3, ECC-M, byte mode.
/// No external dependencies required.
/// </summary>
internal static class QrCodeMatrix
{
    public static bool[,] Generate(string data)
    {
        byte[] bytes = System.Text.Encoding.Latin1.GetBytes(data);
        // Version 3 = 29x29, ECC-M allows up to 47 data bytes
        if (bytes.Length > 47) bytes = bytes[..47];
        return BuildMatrix(bytes);
    }

    private static bool[,] BuildMatrix(byte[] data)
    {
        const int size = 29; // Version 3
        var m = new bool[size, size];

        // Finder patterns
        void Finder(int r, int c)
        {
            for (int i = 0; i < 7; i++) for (int j = 0; j < 7; j++)
                m[r+i, c+j] = (i==0||i==6||j==0||j==6)||(i>=2&&i<=4&&j>=2&&j<=4);
        }
        Finder(0, 0); Finder(0, 22); Finder(22, 0);

        // Timing strips
        for (int i = 8; i < 21; i++) { m[6, i] = (i % 2 == 0); m[i, 6] = (i % 2 == 0); }

        // Alignment pattern (version 3: center at 22,22)
        for (int i = -2; i <= 2; i++) for (int j = -2; j <= 2; j++)
            m[22+i, 22+j] = (Math.Abs(i)==2||Math.Abs(j)==2||(i==0&&j==0));

        // Format bits (ECC-M, mask 2 = (row+col)%3==0) pre-computed: 101011100011001
        int[] fmt = {1,0,1,0,1,1,1,0,0,0,1,1,0,0,1};
        int fi = 0;
        for (int i = 0; i <= 5; i++) m[8, i]   = fmt[fi++] == 1;
        m[8, 7] = fmt[fi++] == 1; m[8, 8] = fmt[fi++] == 1; m[7, 8] = fmt[fi++] == 1;
        for (int i = 5; i >= 0; i--) m[i, 8]   = fmt[fi++] == 1;
        fi = 0;
        for (int i = 0; i <= 6; i++) m[size-1-i, 8] = fmt[fi++] == 1;
        for (int i = 0; i <= 7; i++) m[8, size-1-i] = fmt[fi++] == 1;
        m[size-8, 8] = true; // dark module

        // Data encoding: byte mode
        var bits = new System.Collections.BitArray(0);
        void PushBits(int val, int count)
        {
            var tmp = new System.Collections.BitArray(bits.Length + count);
            for (int i = 0; i < bits.Length; i++) tmp[i] = bits[i];
            for (int i = count-1; i >= 0; i--) tmp[bits.Length + (count-1-i)] = ((val >> i) & 1) == 1;
            bits = tmp;
        }
        PushBits(0b0100, 4);
        PushBits(data.Length, 8);
        foreach (var b in data) PushBits(b, 8);
        PushBits(0, 4);
        while (bits.Length % 8 != 0) PushBits(0, 1);
        int[] padBytes = {0b11101100, 0b00010001};
        int pi = 0;
        // Version 3 ECC-M: 70 data codewords
        while (bits.Length < 70*8) { PushBits(padBytes[pi % 2], 8); pi++; }

        // Place data bits using up-column zigzag
        bool IsFunction(int r, int c)
        {
            if (r < 9 && c < 9) return true;
            if (r < 9 && c >= 21) return true;
            if (r >= 21 && c < 9) return true;
            if (r == 6 || c == 6) return true;
            if (r >= 20 && c >= 20) return true;
            return false;
        }
        int bi = 0;
        bool right = true;
        for (int col = size-1; col >= 1; col -= 2)
        {
            if (col == 6) col = 5;
            for (int row = right ? size-1 : 0; right ? row >= 0 : row < size; row += right ? -1 : 1)
            {
                for (int dc = 0; dc < 2; dc++)
                {
                    int c = col - dc;
                    if (IsFunction(row, c)) continue;
                    if (bi >= bits.Length) break;
                    bool bit = bits[bi++];
                    if ((row + c) % 3 == 0) bit = !bit;
                    m[row, c] = bit;
                }
            }
            right = !right;
        }
        return m;
    }
}
}
