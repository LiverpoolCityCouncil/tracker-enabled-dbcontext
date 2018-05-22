﻿using System.ComponentModel.DataAnnotations;

namespace TrackerEnabledDbContext.EF6.Common.Testing.Models
{
    [TrackChanges]
    public class NormalModel
    {
        [Key]
        public int Id { get; set; }

        public string Description { get; set; }
    }
}