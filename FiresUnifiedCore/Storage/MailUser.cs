namespace FiresCore.Storage
{
    // A mail-system account in the "Users" collection. Field names match Marketplace's DB.CMS_User
    // exactly. UserID is the player's platform id; LastOnline is net time as a long.
    public class MailUser
    {
        public int _id { get; set; }
        public string Name { get; set; }
        public string UserID { get; set; }
        public long LastOnline { get; set; }
        public bool IsOnline { get; set; }
    }
}
