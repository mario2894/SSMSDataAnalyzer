using System;
using SsmsDataAnalyzer.Core.Pivot;
using Xunit;

namespace SsmsDataAnalyzer.Tests.Pivot
{
    public class PeekValueFormatterTests
    {
        [Fact]
        public void ToDisplayText_Null_ReturnsNULL()
        {
            Assert.Equal("NULL", PeekValueFormatter.ToDisplayText(null));
        }

        [Fact]
        public void ToDisplayText_DBNull_ReturnsNULL()
        {
            Assert.Equal("NULL", PeekValueFormatter.ToDisplayText(DBNull.Value));
        }

        [Fact]
        public void ToDisplayText_DateTime_UsesFixedFormat()
        {
            var dt = new DateTime(2026, 1, 2, 3, 4, 5, 678);
            Assert.Equal("2026-01-02 03:04:05.678", PeekValueFormatter.ToDisplayText(dt));
        }

        [Fact]
        public void ToDisplayText_DateTimeOffset_UsesFixedFormat()
        {
            var dto = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
            Assert.Equal("2026-01-02 03:04:05.0000000 +02:00", PeekValueFormatter.ToDisplayText(dto));
        }

        [Fact]
        public void ToDisplayText_TimeSpan_UsesConstantFormat()
        {
            var ts = new TimeSpan(1, 2, 3, 4, 5);
            Assert.Equal(ts.ToString("c"), PeekValueFormatter.ToDisplayText(ts));
        }

        [Theory]
        [InlineData(true, "1")]
        [InlineData(false, "0")]
        public void ToDisplayText_Bool_IsOneOrZero(bool value, string expected)
        {
            Assert.Equal(expected, PeekValueFormatter.ToDisplayText(value));
        }

        [Fact]
        public void ToDisplayText_EmptyByteArray_ReturnsBarePrefix()
        {
            Assert.Equal("0x", PeekValueFormatter.ToDisplayText(new byte[0]));
        }

        [Fact]
        public void ToDisplayText_ByteArray_IsUppercaseHexWithPrefix()
        {
            var bytes = new byte[] { 0x0A, 0xFF, 0x01 };
            Assert.Equal("0x0AFF01", PeekValueFormatter.ToDisplayText(bytes));
        }

        [Fact]
        public void ToDisplayText_ByteArray_TruncatesAfter64BytesWithEllipsis()
        {
            var bytes = new byte[70];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 0xAB;

            var text = PeekValueFormatter.ToDisplayText(bytes);

            Assert.StartsWith("0x", text);
            Assert.EndsWith("…", text);
            // "0x" + 64 bytes * 2 hex chars + the ellipsis character
            Assert.Equal(2 + 64 * 2 + 1, text.Length);
        }

        [Fact]
        public void ToDisplayText_Guid_IsUppercaseDFormat()
        {
            var guid = new Guid("d3f2a1b0-6c9e-4d7a-8b1c-5e2f9a0d4c11");
            Assert.Equal("D3F2A1B0-6C9E-4D7A-8B1C-5E2F9A0D4C11", PeekValueFormatter.ToDisplayText(guid));
        }

        [Fact]
        public void ToDisplayText_Int_UsesInvariantCulture()
        {
            Assert.Equal("42", PeekValueFormatter.ToDisplayText(42));
        }

        [Fact]
        public void ToDisplayText_Decimal_UsesInvariantCulture()
        {
            Assert.Equal("1234.5", PeekValueFormatter.ToDisplayText(1234.5m));
        }

        [Fact]
        public void ToDisplayText_String_ReturnsAsIs()
        {
            Assert.Equal("hello", PeekValueFormatter.ToDisplayText("hello"));
        }

        [Fact]
        public void ToDisplayText_StringLiteralNULL_IsIndistinguishableFromRealNull()
        {
            // Documents the same ambiguity PivotBuilder.NullDisplayText already lives with —
            // a text column whose actual value is the word "NULL" formats the same as a real
            // NULL. Not a bug in this formatter; callers already accept this.
            Assert.Equal("NULL", PeekValueFormatter.ToDisplayText("NULL"));
        }
    }
}
