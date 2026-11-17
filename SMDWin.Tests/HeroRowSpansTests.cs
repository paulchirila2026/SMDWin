using SMDWin.Views;
using Xunit;

namespace SMDWin.Tests
{
    // The card layout in System Information: an incomplete row stretches its last cards, without empty space.
    public class HeroRowSpansTests
    {
        [Theory]
        [InlineData(4, 4, new[] { 1, 1, 1, 1 })]
        [InlineData(3, 4, new[] { 1, 1, 2 })]
        [InlineData(2, 4, new[] { 2, 2 })]
        [InlineData(1, 4, new[] { 4 })]
        [InlineData(2, 3, new[] { 1, 2 })]
        [InlineData(1, 1, new[] { 1 })]
        public void RowSpans_FillTheWholeRow(int count, int columns, int[] expected)
            => Assert.Equal(expected, HeroCards.RowSpans(count, columns));

        [Fact]
        public void RowSpans_AlwaysAddUpToColumns()
        {
            for (int columns = 1; columns <= 5; columns++)
                for (int count = 1; count <= columns; count++)
                    Assert.Equal(columns, System.Linq.Enumerable.Sum(HeroCards.RowSpans(count, columns)));
        }
    }
}
