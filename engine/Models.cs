using System;

namespace TiaEngine
{
    /// <summary>Error model: code + message, thrown by TiaSession on failures.</summary>
    public class TiaException : Exception
    {
        public string Code { get; }
        public TiaException(string code, string message)
            : base(message)
        {
            Code = code;
        }
    }

    /// <summary>Result of importing one block.</summary>
    public class ImportResult
    {
        public string BlockName;
        public string Status;    // created | skipped | failed
        public string Reason;    // detail for skipped/failed
    }
}
