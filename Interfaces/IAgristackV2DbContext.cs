
    using Microsoft.EntityFrameworkCore;
    //using AgristackV2.Domain.Entities;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Collections.Generic;

    namespace AgristackV2.Application.Interfaces
    {
        public interface IAgristackV2DbContext
        {
       // DbSet<User> Users { get; }

        Task<int> SaveChangesAsync(
                CancellationToken cancellationToken = default);
        }
    }


