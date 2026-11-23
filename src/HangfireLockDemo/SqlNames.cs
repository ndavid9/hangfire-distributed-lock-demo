namespace HangfireLockDemo;

public static class SqlNames
{
    public static string RequireDatabaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120)
        {
            throw new ArgumentException("Database name must be a short SQL identifier.", nameof(name));
        }

        foreach (var character in name)
        {
            var ok = character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
            if (!ok)
            {
                throw new ArgumentException("Database name must contain only letters, digits, and underscores.", nameof(name));
            }
        }

        return name;
    }
}
