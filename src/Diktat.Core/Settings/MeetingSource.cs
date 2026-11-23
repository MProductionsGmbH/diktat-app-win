namespace Diktat.Core.Settings;

/// Where the sound of a meeting comes from (MeetingSource.swift). The JSON values are those of
/// the Mac app: "room", "online", "systemOnly".
public enum MeetingSource
{
    /// People in the room, heard by the microphone.
    Room,
    /// An online meeting: the microphone is "Ich", the PC's sound holds the other people.
    Online,
    /// Only the PC's sound, e.g. a webinar or a recording: the microphone stays off.
    SystemOnly,
}

public static class MeetingSourceInfo
{
    public static readonly MeetingSource[] AllCases = [MeetingSource.Room, MeetingSource.Online, MeetingSource.SystemOnly];

    /// The two cards on the meeting page. An online meeting without the own microphone
    /// (<see cref="MeetingSource.SystemOnly"/>) is the online card with its switch turned off.
    public static readonly MeetingSource[] Cards = [MeetingSource.Room, MeetingSource.Online];

    public static bool UsesMicrophone(this MeetingSource source) => source != MeetingSource.SystemOnly;

    public static bool UsesSystemAudio(this MeetingSource source) => source != MeetingSource.Room;

    /// The card this source belongs to.
    public static MeetingSource Card(this MeetingSource source) => source == MeetingSource.Room ? MeetingSource.Room : MeetingSource.Online;

    /// The source the online card stands for, with or without the own microphone.
    public static MeetingSource Online(bool withMicrophone) => withMicrophone ? MeetingSource.Online : MeetingSource.SystemOnly;

    /// The stored choice. Without one, the old switch decides, so nobody loses their setting.
    /// Windows never stored such a switch (it had two start buttons), so the app passes false.
    public static MeetingSource Resolve(MeetingSource? stored, bool legacySystemAudio, bool canRecordSystemAudio)
    {
        var source = stored ?? (legacySystemAudio ? MeetingSource.Online : MeetingSource.Room);
        return canRecordSystemAudio ? source : MeetingSource.Room;
    }

    public static string Title(this MeetingSource source) => source switch
    {
        MeetingSource.Room => L.T("Im Raum"),
        MeetingSource.Online => L.T("Online-Meeting"),
        _ => L.T("Online ohne Mikrofon"),
    };

    public static string Subtitle(this MeetingSource source) => source switch
    {
        MeetingSource.Room => L.T("Mikrofon des Computers"),
        MeetingSource.Online => L.T("System-Audio + Mikrofon"),
        _ => L.T("Nur System-Audio"),
    };

    public static string Explanation(this MeetingSource source) => source switch
    {
        MeetingSource.Room => L.T("Nimmt das Mikrofon des Computers auf, gedacht für Meetings im Raum."),
        MeetingSource.Online => L.T("Die anderen kommen über das System-Audio (was dein PC abspielt, etwa aus Teams, Zoom oder Meet), du über dein Mikrofon. Im Transkript bist du „Ich“. Mit Kopfhörern wird es am saubersten."),
        _ => L.T("Nimmt nur das System-Audio auf, also was dein PC abspielt. Was du selbst sagst, fehlt im Transkript. Gedacht für Webinare und Videos."),
    };
}
