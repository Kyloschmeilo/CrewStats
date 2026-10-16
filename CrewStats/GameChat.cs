using AmongUs.Data;
using InnerNet;

namespace CrewStats;

/// <summary>
/// Chat-Hinweise der Mod (Rollenwünsche, Kein Skip bei Notfall). Freien Text darf nur senden, wer im Spiel
/// „Free Chat“ eingestellt hat: Mit „Nur Quick Chat“ wertet der Among-Us-Server jede freie Chat-Nachricht als
/// Hack, wirft den Spieler raus und sperrt ihn für die Lobby. Außerdem gilt dieselbe Sende-Pause wie beim
/// normalen Chat (3 s), damit der Server nichts als Spam ansieht.
/// </summary>
internal static class GameChat
{
    public static bool FreeChatAllowed =>
        DataManager.Settings.Multiplayer.ChatMode == QuickChatModes.FreeChatOrQuickChat;

    /// <summary>Für alle in den Chat schreiben. false = geht gerade nicht (kein Free Chat oder Sende-Pause).</summary>
    public static bool TrySend(string text)
    {
        var local = PlayerControl.LocalPlayer;
        var chat = HudManager.InstanceExists ? HudManager.Instance.Chat : null;
        if (!FreeChatAllowed || local == null || chat == null) return false;
        if (chat.timeSinceLastMessage < ChatController.MAX_CHAT_SEND_RATE) return false;
        if (!local.RpcSendChat(text)) return false;
        chat.timeSinceLastMessage = 0f;
        return true;
    }

    /// <summary>Hinweis nur im eigenen Chat anzeigen (geht nicht an den Server).</summary>
    public static void ShowLocal(string text)
    {
        var chat = HudManager.InstanceExists ? HudManager.Instance.Chat : null;
        if (chat != null) chat.AddChatWarning(text);
    }
}
