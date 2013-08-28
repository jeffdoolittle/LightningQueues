using System;
using System.IO;
using System.Runtime.ConstrainedExecution;
using System.Threading;
using FubuCore.Logging;
using Microsoft.Isam.Esent.Interop;

namespace LightningQueues.Storage
{
	public class QueueStorage : CriticalFinalizerObject, IDisposable
	{
		private JET_INSTANCE _instance;
	    private readonly string _database;
	    private readonly string _path;
	    private ColumnsInformation _columnsInformation;
	    private readonly QueueManagerConfiguration _configuration;
	    private readonly ILogger _logger;

	    private readonly ReaderWriterLockSlim _usageLock = new ReaderWriterLockSlim();

		public Guid Id { get; private set; }

		public QueueStorage(string database, QueueManagerConfiguration configuration, ILogger logger)
		{
		    _configuration = configuration;
		    _logger = logger;
		    _database = database;
		    _path = database;
			if (Path.IsPathRooted(database) == false)
				_path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, database);
			_database = Path.Combine(_path, Path.GetFileName(database));
			Api.JetCreateInstance(out _instance, database + Guid.NewGuid());
		}

		public void Initialize()
		{
			ConfigureInstance(_instance);
			try
			{
				Api.JetInit(ref _instance);

				EnsureDatabaseIsCreatedAndAttachToDatabase();

				SetIdFromDb();

				LoadColumnInformation();
			}
			catch (Exception e)
			{
				Dispose();
				throw new InvalidOperationException("Could not open queue: " + _database, e);
			}
		}

		private void LoadColumnInformation()
		{
			_columnsInformation = new ColumnsInformation();
			_instance.WithDatabase(_database, (session, dbid) =>
			{
				using (var table = new Table(session, dbid, "subqueues", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.SubqueuesColumns = Api.GetColumnDictionary(session, table);
				}
				using (var table = new Table(session, dbid, "outgoing_history", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.OutgoingHistoryColumns = Api.GetColumnDictionary(session, table);
				}
				using (var table = new Table(session, dbid, "outgoing", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.OutgoingColumns = Api.GetColumnDictionary(session, table);
				}
				using (var table = new Table(session, dbid, "recovery", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.RecoveryColumns = Api.GetColumnDictionary(session, table);
				}
				using (var table = new Table(session, dbid, "transactions", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.TxsColumns = Api.GetColumnDictionary(session, table);
				}
				using (var table = new Table(session, dbid, "queues", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.QueuesColumns = Api.GetColumnDictionary(session, table);
				}
				using (var table = new Table(session, dbid, "recveived_msgs", OpenTableGrbit.ReadOnly))
				{
					_columnsInformation.RecveivedMsgsColumns = Api.GetColumnDictionary(session, table);
				}
			});
		}

		private void ConfigureInstance(JET_INSTANCE jetInstance)
		{
			new InstanceParameters(jetInstance)
			{
				CircularLog = true,
				Recovery = true,
				CreatePathIfNotExist = true,
				TempDirectory = Path.Combine(_path, "temp"),
				SystemDirectory = Path.Combine(_path, "system"),
				LogFileDirectory = Path.Combine(_path, "logs"),
				MaxVerPages = 8192,
				MaxTemporaryTables = 8192
			};
		}

		private void SetIdFromDb()
		{
			try
			{
				_instance.WithDatabase(_database, (session, dbid) =>
				{
					using (var details = new Table(session, dbid, "details", OpenTableGrbit.ReadOnly))
					{
						Api.JetMove(session, details, JET_Move.First, MoveGrbit.None);
						var columnids = Api.GetColumnDictionary(session, details);
						var column = Api.RetrieveColumn(session, details, columnids["id"]);
						Id = new Guid(column);
						var schemaVersion = Api.RetrieveColumnAsString(session, details, columnids["schema_version"]);
						if (schemaVersion != SchemaCreator.SchemaVersion)
							throw new InvalidOperationException("The version on disk (" + schemaVersion + ") is different that the version supported by this library: " + SchemaCreator.SchemaVersion + Environment.NewLine +
																"You need to migrate the disk version to the library version, alternatively, if the data isn't important, you can delete the file and it will be re-created (with no data) with the library version.");
					}
				});
			}
			catch (Exception e)
			{
				throw new InvalidOperationException("Could not read db details from disk. It is likely that there is a version difference between the library and the db on the disk." + Environment.NewLine +
													"You need to migrate the disk version to the library version, alternatively, if the data isn't important, you can delete the file and it will be re-created (with no data) with the library version.", e);
			}
		}

		private void EnsureDatabaseIsCreatedAndAttachToDatabase()
		{
			using (var session = new Session(_instance))
			{
				try
				{
					Api.JetAttachDatabase(session, _database, AttachDatabaseGrbit.None);
					return;
				}
				catch (EsentErrorException e)
				{
					if (e.Error == JET_err.DatabaseDirtyShutdown)
					{
						try
						{
							using (var recoverInstance = new Instance("Recovery instance for: " + _database))
							{
								recoverInstance.Init();
								using (var recoverSession = new Session(recoverInstance))
								{
									ConfigureInstance(recoverInstance.JetInstance);
									Api.JetAttachDatabase(recoverSession, _database,
														  AttachDatabaseGrbit.DeleteCorruptIndexes);
									Api.JetDetachDatabase(recoverSession, _database);
								}
							}
						}
						catch (Exception)
						{
						}

						Api.JetAttachDatabase(session, _database, AttachDatabaseGrbit.None);
						return;
					}
					if (e.Error != JET_err.FileNotFound)
						throw;
				}

				new SchemaCreator(session).Create(_database);
				Api.JetAttachDatabase(session, _database, AttachDatabaseGrbit.None);
			}
		}

		public void Dispose()
		{
			_usageLock.EnterWriteLock();
			try
			{
				_logger.Debug("Disposing queue storage");
				try
				{
					Api.JetTerm2(_instance, TermGrbit.Complete);
					GC.SuppressFinalize(this);
				}
				catch (Exception e)
				{
					_logger.Error("Could not dispose of queue storage properly", e);
					throw;
				}
			}
			finally
			{
				_usageLock.ExitWriteLock();
			}
		}

		public void DisposeRudely()
		{
			_usageLock.EnterWriteLock();
			try
			{
				_logger.Debug("Rudely disposing queue storage");
				try
				{
					Api.JetTerm2(_instance, TermGrbit.Abrupt);
					GC.SuppressFinalize(this);
				}
				catch (Exception e)
				{
					_logger.Error("Could not dispose of queue storage properly", e);
					throw;
				}
			}
			finally
			{
				_usageLock.ExitWriteLock();
			}
		}


		~QueueStorage()
		{
			try
			{
				_logger.Info("Disposing esent resources from finalizer! You should call QueueStorage.Dispose() instead!");
				Api.JetTerm2(_instance, TermGrbit.Complete);
			}
			catch (Exception exception)
			{
				try
				{
					_logger.Error("Failed to dispose esent instance from finalizer, trying abrupt termination.", exception);
					try
					{
						Api.JetTerm2(_instance, TermGrbit.Abrupt);
					}
					catch (Exception e)
					{
						_logger.Error("Could not dispose esent instance abruptly", e);
					}
				}
				catch
				{
				}
			}
		}

		public void Global(Action<GlobalActions> action)
		{
			var shouldTakeLock = _usageLock.IsReadLockHeld == false;
			try
			{
				if (shouldTakeLock)
					_usageLock.EnterReadLock();
				using (var qa = new GlobalActions(_instance, _columnsInformation, _database, Id, _configuration, _logger))
				{
					action(qa);
				}
			}
			finally 
			{
				if(shouldTakeLock)
					_usageLock.ExitReadLock();
			}
		}

		public void Send(Action<SenderActions> action)
		{
			var shouldTakeLock = _usageLock.IsReadLockHeld == false;
			try
			{
				if (shouldTakeLock)
					_usageLock.EnterReadLock();
				using (var qa = new SenderActions(_instance, _columnsInformation, _database, Id, _configuration, _logger))
				{
					action(qa);
				}
			}
			finally
			{
				if (shouldTakeLock)
					_usageLock.ExitReadLock();
			}
		}
	}
}