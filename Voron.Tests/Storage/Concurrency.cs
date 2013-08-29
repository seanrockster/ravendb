﻿namespace Voron.Tests.Storage
{
	using System.IO;

	using Xunit;

	public class Concurrency : StorageTest
	{
		[Fact]
		public void MissingEntriesShouldReturn0Version()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				Assert.Equal(0, Env.Root.ReadVersion(tx, "key/1"));
			}
		}

		[Fact]
		public void SimpleVersion()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				Env.Root.Add(tx, "key/1", StreamFor("123"));
				Assert.Equal(1, Env.Root.ReadVersion(tx, "key/1"));
				Env.Root.Add(tx, "key/1", StreamFor("123"));
				Assert.Equal(2, Env.Root.ReadVersion(tx, "key/1"));

				tx.Commit();
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				Assert.Equal(2, Env.Root.ReadVersion(tx, "key/1"));
			}
		}

		[Fact]
		public void VersionOverflow()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				for (uint i = 1; i <= ushort.MaxValue + 1; i++)
				{
					Env.Root.Add(tx, "key/1", StreamFor("123"));

					var expected = i;
					if (expected > ushort.MaxValue)
						expected = 1;

					Assert.Equal(expected, Env.Root.ReadVersion(tx, "key/1"));
				}

				tx.Commit();
			}
		}

		[Fact]
		public void NoCommit()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				Env.Root.Add(tx, "key/1", StreamFor("123"));
				Assert.Equal(1, Env.Root.ReadVersion(tx, "key/1"));
				Env.Root.Add(tx, "key/1", StreamFor("123"));
				Assert.Equal(2, Env.Root.ReadVersion(tx, "key/1"));
			}

			using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				Assert.Equal(0, Env.Root.ReadVersion(tx, "key/1"));
			}
		}

		[Fact]
		public void Delete()
		{
			using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				Env.Root.Add(tx, "key/1", StreamFor("123"));
				Assert.Equal(1, Env.Root.ReadVersion(tx, "key/1"));

				Env.Root.Delete(tx, "key/1");
				Assert.Equal(0, Env.Root.ReadVersion(tx, "key/1"));
			}
		}
	}
}