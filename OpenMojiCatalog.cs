namespace Cipher;

public sealed record OpenMojiEntry(string Code, string Emoji, string Label) {
    public string PackUri => $"pack://application:,,,/Assets/OpenMoji/{Code}.png";
}

public static class OpenMojiCatalog {
    public static IReadOnlyList<OpenMojiEntry> Entries { get; } = new[] {
        new OpenMojiEntry("1F60A", "😊", "smile"),
        new OpenMojiEntry("1F609", "😉", "wink"),
        new OpenMojiEntry("1F60D", "😍", "heart eyes"),
        new OpenMojiEntry("1F602", "😂", "laugh tears"),
        new OpenMojiEntry("1F62D", "😭", "cry"),
        new OpenMojiEntry("1F92D", "🤭", "giggle"),
        new OpenMojiEntry("1F97A", "🥺", "pleading eyes"),
        new OpenMojiEntry("1F914", "🤔", "thinking"),
        new OpenMojiEntry("1F60E", "😎", "cool"),
        new OpenMojiEntry("1F525", "🔥", "fire"),
        new OpenMojiEntry("2728", "✨", "sparkles"),
        new OpenMojiEntry("1F338", "🌸", "blossom"),
        new OpenMojiEntry("1F33F", "🌿", "herb"),
        new OpenMojiEntry("1F31F", "🌟", "star"),
        new OpenMojiEntry("1F30A", "🌊", "wave"),
        new OpenMojiEntry("1F44D", "👍", "thumbs up"),
        new OpenMojiEntry("1F44F", "👏", "clap"),
        new OpenMojiEntry("1F4AC", "💬", "chat"),
        new OpenMojiEntry("1F680", "🚀", "rocket"),
        new OpenMojiEntry("1F4AF", "💯", "hundred")
    };
}
