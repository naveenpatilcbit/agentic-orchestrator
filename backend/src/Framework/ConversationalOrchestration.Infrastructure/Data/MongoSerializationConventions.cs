using System.Threading;
using MongoDB.Bson.Serialization.Conventions;

namespace ConversationalOrchestration.Infrastructure.Data;

internal static class MongoSerializationConventions
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        var pack = new ConventionPack
        {
            new IgnoreExtraElementsConvention(true)
        };

        ConventionRegistry.Register(
            "ConversationalOrchestration.IgnoreExtraElements",
            pack,
            _ => true);
    }
}

