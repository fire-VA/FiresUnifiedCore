using System.Collections.Generic;

namespace FiresCore.Storage
{
    // One mail message in the "Mails" collection. Field names and the ExpireAfterMinutes default match
    // Marketplace's DB.CMS_Entry exactly. Owner is the recipient's platform id; Attachments are claimable
    // items, Links are reference-only previews.
    public class MailEntry
    {
        public const int DefaultExpireAfterMinutes = 10800;

        public int _id { get; set; }
        public string Owner { get; set; }
        public long Created { get; set; }
        public string Sender { get; set; }
        public string Topic { get; set; }
        public string Message { get; set; }
        public List<StoredItem> Attachments { get; set; }
        public List<StoredItem> Links { get; set; }
        public int ExpireAfterMinutes { get; set; } = DefaultExpireAfterMinutes;
        public bool WasRead { get; set; }
        public bool AttachmentsTaken { get; set; }
    }
}
