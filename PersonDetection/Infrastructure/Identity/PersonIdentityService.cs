// PersonDetection.Infrastructure/Identity/PersonIdentityService.cs
namespace PersonDetection.Infrastructure.Identity
{
    using System.Collections.Concurrent;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using PersonDetection.Application.Configuration;
    using PersonDetection.Domain.Entities;
    using PersonDetection.Domain.Services;
    using PersonDetection.Domain.ValueObjects;
    using PersonDetection.Infrastructure.Context;

    public class PersonIdentityService : IPersonIdentityMatcher
    {
        private readonly ConcurrentDictionary<Guid, PersonIdentity> _identities = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly float _similarityThreshold;
        private readonly ILogger<PersonIdentityService> _logger;

        private class PersonIdentity
        {
            public Guid GlobalPersonId { get; set; }
            public int DbId { get; set; }
            public float[] FeatureVector { get; set; } = null!;
            public DateTime LastSeen { get; set; }
            public int CameraId { get; set; }
        }

        public PersonIdentityService(
            IServiceProvider serviceProvider,
            float similarityThreshold,
            ILogger<PersonIdentityService> logger)
        {
            _serviceProvider = serviceProvider;
            _similarityThreshold = similarityThreshold;
            _logger = logger;

            // Load existing identities from database
            Task.Run(LoadExistingIdentities);
        }

        private async Task LoadExistingIdentities()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<DetectionContext>();

                var recentPersons = await context.UniquePersons
                    .Where(p => p.IsActive && p.LastSeenAt >= DateTime.UtcNow.AddHours(-1))
                    .ToListAsync();

                foreach (var person in recentPersons)
                {
                    var features = person.GetFeatureArray();
                    if (features != null)
                    {
                        _identities[person.GlobalPersonId] = new PersonIdentity
                        {
                            GlobalPersonId = person.GlobalPersonId,
                            DbId = person.Id,
                            FeatureVector = features,
                            LastSeen = person.LastSeenAt,
                            CameraId = person.LastSeenCameraId
                        };
                    }
                }

                _logger.LogInformation("Loaded {Count} existing identities from database", _identities.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load existing identities");
            }
        }

        public Guid GetOrCreateIdentity(FeatureVector vector)
        {
            return GetOrCreateIdentity(vector, 0);
        }

        public Guid GetOrCreateIdentity(FeatureVector vector, int cameraId)
        {
            if (TryMatch(vector, out var personId, out var similarity))
            {
                UpdateIdentity(personId, vector, cameraId);
                _logger.LogDebug("Matched existing person {PersonId} with similarity {Similarity:P0}",
                    personId, similarity);
                return personId;
            }

            // Create new identity
            var newId = Guid.NewGuid();
            _identities[newId] = new PersonIdentity
            {
                GlobalPersonId = newId,
                DbId = 0, // Will be set after DB save
                FeatureVector = vector.Values,
                LastSeen = DateTime.UtcNow,
                CameraId = cameraId
            };

            _logger.LogInformation("🆕 New person identity created: {PersonId}", newId);
            return newId;
        }

        public bool TryMatch(FeatureVector vector, out Guid personId, out float similarity)
        {
            personId = Guid.Empty;
            similarity = 0;

            float maxSimilarity = 0;
            Guid bestMatch = Guid.Empty;

            foreach (var (id, identity) in _identities)
            {
                var storedVector = new FeatureVector(identity.FeatureVector);
                var sim = vector.CosineSimilarity(storedVector);

                if (sim > maxSimilarity)
                {
                    maxSimilarity = sim;
                    bestMatch = id;
                }
            }

            if (maxSimilarity >= _similarityThreshold)
            {
                personId = bestMatch;
                similarity = maxSimilarity;
                return true;
            }

            return false;
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector)
        {
            UpdateIdentity(personId, vector, 0);
        }

        public void UpdateIdentity(Guid personId, FeatureVector vector, int cameraId)
        {
            if (_identities.TryGetValue(personId, out var existing))
            {
                // Exponential moving average for feature update
                var alpha = 0.1f;
                var updated = new float[vector.Dimension];
                for (int i = 0; i < vector.Dimension; i++)
                {
                    updated[i] = existing.FeatureVector[i] * (1 - alpha) + vector.Values[i] * alpha;
                }

                existing.FeatureVector = updated;
                existing.LastSeen = DateTime.UtcNow;
                if (cameraId > 0) existing.CameraId = cameraId;
            }
        }

        public void SetDbId(Guid personId, int dbId)
        {
            if (_identities.TryGetValue(personId, out var identity))
            {
                identity.DbId = dbId;
            }
        }

        public int GetDbId(Guid personId)
        {
            return _identities.TryGetValue(personId, out var identity) ? identity.DbId : 0;
        }

        public int GetActiveIdentityCount() => _identities.Count;

        public void CleanupExpired(TimeSpan expirationTime)
        {
            var threshold = DateTime.UtcNow - expirationTime;
            var expired = _identities
                .Where(kvp => kvp.Value.LastSeen < threshold)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in expired)
            {
                _identities.TryRemove(id, out _);
            }

            if (expired.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired identities", expired.Count);
            }
        }
    }
}