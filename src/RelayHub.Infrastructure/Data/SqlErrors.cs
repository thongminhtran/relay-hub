using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace RelayHub.Infrastructure.Data;

public static class SqlErrors
{
    /// <summary>2601 = duplicate key in unique index, 2627 = unique constraint violation.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException is SqlException { Number: 2601 or 2627 };
}
