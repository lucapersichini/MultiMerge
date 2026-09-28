// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using Xunit;

namespace MultiMerge.Tests
{
    public class JsonParserTests
    {
        [Fact]
        public void WhenValueHasNewLine_ShouldCorrectParse()
        {
            var value = string.Format("{0}Patch Back", Environment.NewLine);
            var json = string.Format("{{\"comment_format\": \"{0}\"}}", value);

            var values = JsonParser.ParseJson(json);

            Assert.NotEmpty(values);
            Assert.Equal(value, values["comment_format"]);
        }
    }
}
