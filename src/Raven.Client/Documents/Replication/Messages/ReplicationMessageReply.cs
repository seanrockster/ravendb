﻿using System;
using Sparrow.Json.Parsing;

namespace Raven.Client.Documents.Replication.Messages
{
    internal class ReplicationMessageReply
    {
        internal enum ReplyType
        {
            None,
            Ok,
            Error
        }

        public ReplyType Type { get; set; }

        public long LastEtagAccepted { get; set; }

        public string Exception { get; set; }

        public string Message { get; set; }

        public string MessageType { get; set; }

        public ChangeVectorEntry[] ChangeVector { get; set; }

        public string DatabaseId { get; set; }
    }

    public static class ChangeVectorExtensions
    {
        public static DynamicJsonArray ToJson(this ChangeVectorEntry[] self)
        {
            var results = new DynamicJsonArray();
            foreach (var entry in self)
                results.Add(entry.ToJson());
            return results;
        }
    }

    public struct ChangeVectorEntry : IComparable<ChangeVectorEntry>, IDynamicJson
    {
        public Guid DbId;
        public long Etag;

        public override string ToString()
        {
            return DbId.ToString().Substring(0, 4) + "... " + Etag;
        }

        public bool Equals(ChangeVectorEntry other)
        {
            return DbId.Equals(other.DbId) && Etag == other.Etag;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (DbId.GetHashCode()*397) ^ Etag.GetHashCode();
            }
        }

        // we use it to sort change vectors by the ID.
        public int CompareTo(ChangeVectorEntry other)
        {
            var rc = DbId.CompareTo(other.DbId);
            if (rc != 0)
                return rc;
            return Etag.CompareTo(other.Etag);
        }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(DbId)] = DbId.ToString(),
                [nameof(Etag)] = Etag
            };
        }
    }
}
