using KSP.Localization;

namespace KRAB.Graph
{
	public enum IssueSeverity
	{
		Info,
		Warning,
		Error
	}

	/// <summary>
	/// A single graph validation finding. <see cref="message"/> is plain English and used
	/// only for logging, which stays English whatever the player's language.
	/// <see cref="LocalizedText"/> is the in-UI equivalent, keyed on <see cref="code"/>.
	/// </summary>
	public class ValidationIssue
	{
		public readonly IssueSeverity severity;
		public readonly string code;
		public readonly string message;
		public readonly string[] args;

		/// <summary>Id of the node the issue is about, when node-specific (UI highlight).</summary>
		public readonly string nodeId;

		public ValidationIssue(IssueSeverity severity, string code, string message, string nodeId = null,
			params string[] args)
		{
			this.severity = severity;
			this.code = code;
			this.message = message;
			this.nodeId = nodeId;
			this.args = args ?? new string[0];
		}

		/// <summary>Localized text for the editor's validation strip, from
		/// #LOC_KRAB_issue_&lt;code&gt; plus args. Falls back to the English log
		/// message when a code has no entry.</summary>
		public string LocalizedText()
		{
			string localized = Localizer.Format("#LOC_KRAB_issue_" + code, args);
			return localized.StartsWith("#LOC_KRAB_") ? message : localized;
		}

		public override string ToString()
		{
			return "[" + severity + "] " + code + ": " + message;
		}
	}
}
