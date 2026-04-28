using AutoFixture.Xunit2;

namespace GoldSrcOps.UnitTests.Helpers;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InlineAutoMoqDataAttribute : CompositeDataAttribute
{
    public InlineAutoMoqDataAttribute(params object[] values)
        : base(new InlineDataAttribute(values), new AutoMoqDataAttribute())
    {
    }
}
