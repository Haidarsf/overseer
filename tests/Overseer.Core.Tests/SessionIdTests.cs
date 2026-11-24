using Overseer.Core;
using Xunit;

namespace Overseer.Core.Tests;

public class SessionIdTests
{
    [Fact]
    public void Two_transcripts_with_the_same_session_id_do_not_crash_lookups()
    {
        // resuming or copying a session can leave two files with one id (this crashed 0.3.0 at startup)
        var newer = new Session { Id = "e4b5fb97", FilePath = "projects/a/e4b5fb97.jsonl" };
        var older = new Session { Id = "e4b5fb97", FilePath = "projects/b/e4b5fb97.jsonl" };
        var other = new Session { Id = "11111111", FilePath = "projects/a/11111111.jsonl" };

        var byId = SessionStore.ById(new[] { newer, older, other });

        Assert.Equal(2, byId.Count);
        Assert.Same(newer, byId["e4b5fb97"]);   // the first (most relevant) one wins
        Assert.Same(other, byId["11111111"]);
    }
}
