namespace WhisperDesk.Transcript.Contract;

public interface ITranscriptionHistoryService
{
    Task WriteEntryAsync(TranscriptionHistoryEntry entry);
    string GetHistoryDirectory();
}
