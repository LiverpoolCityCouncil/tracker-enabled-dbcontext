﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using TrackerEnabledDbContext.Common.Configuration;
using TrackerEnabledDbContext.Common.EventArgs;
using TrackerEnabledDbContext.Common.Models;
using TrackerEnabledDbContext.Core.Common.Auditors;
using TrackerEnabledDbContext.Core.Common.Interfaces;

namespace TrackerEnabledDbContext.Core.Common
{
    public class CoreTracker
    {
        public event EventHandler<AuditLogGeneratedEventArgs> OnAuditLogGenerated;

        private readonly ITrackerContext _context;

        public CoreTracker(ITrackerContext context)
        {
            _context = context;
        }

        public void AuditAdditions(object userName, IEnumerable<EntityEntry> addedEntries, ExpandoObject metadata)
        {
            List<AuditLog> records = new List<AuditLog>();

            // Get all Added entities
            foreach (EntityEntry ent in addedEntries)
            {
                using (var auditer = new LogAuditor(ent))
                {
                    AuditLog record = auditer.CreateLogRecord(userName, EventType.Added, _context, metadata);
                    if (record != null)
                    {
                        var arg = new AuditLogGeneratedEventArgs(record, ent.Entity, metadata);
                        RaiseOnAuditLogGenerated(this, arg);
                        if (!arg.SkipSavingLog)
                        {
                            records.Add(record);
                        }
                    }
                }
            }

            _context.AuditLogs.AddRange(records);
        }

        public void AuditModifications(object userName, ExpandoObject metadata)
        {
            List<AuditLog> records = new List<AuditLog>();

            // Get all Modified entities (not Unmodified or Deleted or Detached or Added)
            foreach (EntityEntry ent in _context.ChangeTracker.Entries().Where(p => p.State == EntityState.Modified))
            {
                using (var auditer = new LogAuditor(ent))
                {
                    var eventType = GetEventType(ent);
                    
                    AuditLog record = auditer.CreateLogRecord(userName, eventType, _context, metadata);

                    if (record != null)
                    {
                        var arg = new AuditLogGeneratedEventArgs(record, ent.Entity, metadata);
                        RaiseOnAuditLogGenerated(this, arg);
                        if (!arg.SkipSavingLog)
                        {
                            records.Add(record);
                        }
                    }
                }
            }

            _context.AuditLogs.AddRange(records);
        }

        public void AuditDeletions(object userName, ExpandoObject metadata)
        {
            List<AuditLog> records = new List<AuditLog>();

            // Get all Deleted or Modified entities (not Unmodified or Detached or Added)
            foreach (EntityEntry ent in _context.ChangeTracker.Entries().Where(p => p.State == EntityState.Deleted))
            {
                using (var auditer = new LogAuditor(ent))
                {
                    var eventType = GetEventType(ent);                    
                    AuditLog record = auditer.CreateLogRecord(userName, eventType, _context, metadata);

                    if (record != null)
                    {
                        var arg = new AuditLogGeneratedEventArgs(record, ent.Entity, metadata);
                        RaiseOnAuditLogGenerated(this, arg);
                        if (!arg.SkipSavingLog)
                        {
                            records.Add(record);
                        }
                    }
                }
            }

            _context.AuditLogs.AddRange(records);
        }

        public IEnumerable<EntityEntry> GetAdditions()
        {
            return _context.ChangeTracker.Entries().Where(p => p.State == EntityState.Added).ToList();
        }

        /// <summary>
        ///     Get all logs for the given model type
        /// </summary>
        /// <typeparam name="TEntity">Type of domain model</typeparam>
        /// <returns></returns>
        public IQueryable<AuditLog> GetLogs<TEntity>()
        {
            IEnumerable<string> entityTypeNames = EntityTypeNames<TEntity>();
            return _context.AuditLogs.Where(x => entityTypeNames.Contains(x.TypeFullName));
        }

        /// <summary>
        ///     Get all logs for the enitity type name
        /// </summary>
        /// <param name="context"></param>
        /// <param name="entityTypeName">Name of entity type</param>
        /// <returns></returns>
        public IQueryable<AuditLog> GetLogs(string entityTypeName)
        {
            return _context.AuditLogs.Where(x => x.TypeFullName == entityTypeName);
        }

        /// <summary>
        ///     Get all logs for the given model type for a specific record
        /// </summary>
        /// <typeparam name="TEntity">Type of domain model</typeparam>
        /// <param name="context"></param>
        /// <param name="primaryKey">primary key of record</param>
        /// <returns></returns>
        public IQueryable<AuditLog> GetLogs<TEntity>(object primaryKey)
        {
            string key = primaryKey.ToString();
            IEnumerable<string> entityTypeNames = EntityTypeNames<TEntity>();

            return _context.AuditLogs.Where(x => entityTypeNames.Contains(x.TypeFullName) && x.RecordId == key);
        }

        /// <summary>
        ///     Get all logs for the given entity name for a specific record
        /// </summary>
        /// <param name="context"></param>
        /// <param name="entityTypeName">entity type name</param>
        /// <param name="primaryKey">primary key of record</param>
        /// <returns></returns>
        public IQueryable<AuditLog> GetLogs(string entityTypeName, object primaryKey)
        {
            string key = primaryKey.ToString();
            return _context.AuditLogs.Where(x => x.TypeFullName == entityTypeName && x.RecordId == key);
        }

        private EventType GetEventType(EntityEntry entry)
        {
            if (entry.State == EntityState.Deleted)
                return EventType.Deleted;

            var isSoftDeletable = GlobalTrackingConfig.SoftDeletableType?.IsInstanceOfType(entry.Entity);

            if (isSoftDeletable.HasValue && isSoftDeletable.Value)
            {
                var previouslyDeleted = GlobalTrackingConfig.DisconnectedContext ?
                    (bool)entry.GetDatabaseValues().GetValue<object>(GlobalTrackingConfig.SoftDeletablePropertyName) :
                    (bool)entry.Property(GlobalTrackingConfig.SoftDeletablePropertyName).OriginalValue;

                var nowDeleted = (bool)entry.CurrentValues[GlobalTrackingConfig.SoftDeletablePropertyName];

                if (previouslyDeleted && !nowDeleted)
                {
                    return EventType.UnDeleted;
                }

                if (!previouslyDeleted && nowDeleted)
                {
                    return EventType.SoftDeleted;
                }
            }

            return EventType.Modified;
        }

        private IEnumerable<string> EntityTypeNames<TEntity>()
        {
            Type entityType = typeof(TEntity);
            return typeof(TEntity).Assembly.GetTypes()
                .Where(t => t.IsSubclassOf(entityType) || t.BaseType.FullName == entityType.BaseType.FullName).Select(m => m.BaseType.FullName);
        }

        protected virtual void RaiseOnAuditLogGenerated(object sender, AuditLogGeneratedEventArgs e)
        {
            OnAuditLogGenerated?.Invoke(sender, e);
        }
    }
}
