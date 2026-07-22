namespace KRAB.Graph
{
	public enum IssueSeverity
	{
		Info,
		Warning,
		Error
	}

	/// <summary>
	/// A single graph validation finding. Messages are English log text for now;
	/// the editor UI milestone will map <see cref="code"/> to #LOC_KRAB_ keys so
	/// they can be shown localized in-game.
	/// </summary>
	public class ValidationIssue
	{
		public readonly IssueSeverity severity;
		public readonly string code;
		public readonly string message;

		/// <summary>Id of the node the issue is about, when node-specific (UI highlight).</summary>
		public readonly string nodeId;

		public ValidationIssue(IssueSeverity severity, string code, string message, string nodeId = null)
		{
			this.severity = severity;
			this.code = code;
			this.message = message;
			this.nodeId = nodeId;
		}

		public override string ToString()
		{
			return "[" + severity + "] " + code + ": " + message;
		}
	}
}
