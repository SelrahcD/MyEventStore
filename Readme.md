# MyDotNetEventStore

This is a .Net Event Store built on top PostgresSQL for learning purpose.

**⚠️ This is a toy project, do not use in production.**
** ⚠️⚠️ Seriously, do not use in production. We might have some concurrency issues.**

It takes inspiration from various sources such as:
- [EventStoreDb](https://www.eventstore.com/)
- [Marten](https://martendb.io/)
- [Emmett](https://event-driven-io.github.io/emmett/)



## Todo

The following todo list comes from the article [Requirements for the storage of events](https://www.eventstore.com/blog/requirements-for-the-storage-of-events) from Yves Lorphelin. I probably won't implement everything but it serves as a good list of ideas of thing that could be cool to play with.

## Events
- [x] Ensure each event has the following attributes:
    - [x] **Type**: A string defined by the application.
    - [x] **Stream**: The stream the event belongs to, controlled by the application.
    - [x] **Id**: A unique string over the store (UUID).
    - [x] **Revision**: A number representing the position in the stream.
    - [x] **Positions**: Numbers representing the global position of the event in the stream structure.
    - [x] **Data**: The payload of the event as JSON
    - [x] **Metadata**: The metadata of the event as JSON
        - [ ] System Metadata:
            - [ ] **Timestamp**: Date and time when the event was appended.
            - [ ] **CorrelationId**: Supplied by the application.
            - [ ] **CausationId**: Supplied by the application.
        - [x] Application Metadata: Application-level metadata in JSON
- [x] Use Revision for optimistic locking.
- [x] Manage Revision & Positions as strictly increasing values.

## Storage Requirements
- [ ] Enforce storage requirements under high load conditions.
- [ ] Support 10,000 events/second across multiple clients.
- [ ] Scale to 10 million streams and up to 500 million events.

## Streams
- [ ] Define streams to represent entities with structured naming.
- [ ] Attributes for streams:
    - [ ] **Schema**: Similar to a table schema in relational databases.
    - [ ] **Category**: Identifies the entity type.
    - [ ] **Id**: Uniquely identifies a stream instance.
    - [ ] **Metadata**:
        - [ ] System Metadata:
            - [ ] **Time To Live**: Maximum age of events.
            - [ ] **Maximum Count**: Maximum number of events in the stream.
        - [ ] Application Metadata: Application-level metadata.
- [ ] Use fully qualified names like [Schema].[Category].[Id].
- [ ] Support hierarchical stream levels: [Schema].[Category], [Schema], and All.
- [ ] Implement automatic deletion based on Time To Live and Maximum Count.

## Operations
### Appending Events
- [x] Implement append operation: `Append(Stream, ExpectedRevision, Event[]) -> Result`.
- [x] Implement append operation: `Append(Stream, ExpectedRevision, Event) -> Result`.
- [x] Use ExpectedRevision for optimistic locking:
    - [x] **Any**: No concurrency check.
    - [x] **NoStream**: Stream should not exist.
    - [x] **StreamExists**: Stream exists, may be empty.
    - [x] **Some number**: Match specific stream revision.
- [x] Return new Revision and Positions after appending.

### Idempotency
- [x] Implement idempotency checks for append operations.
- [ ] Use ExpectedRevision and EventId for idempotency behavior.

### Appending Metadata
- [ ] Allow appending metadata to streams: `AppendMetadata(Stream, ExpectedRevision, Metadata) -> Result`.
- [ ] Support appending even if the target stream does not exist.

### Reading Data and Metadata
- [x] Implement read operations with forward or backward directions:
    - [x] Read Stream
      - [x] Forward
        - [x] Without position
        - [x] With position
      - [x] Backward
        - [x] Without position
        - [x] With position
  - [x] Read All Streams
      - [x] Forward
          - [x] Without position
          - [x] With position
      - [x] Backward
          - [x] Without position
          - [x] With position
  - [ ] Read events by correlationId
  - [ ] Read events by causationId
  
- [ ] Support reading from:
    - [ ] Schema level.
    - [ ] Category level.
    - [ ] Fully qualified stream names.

### Truncating and Deleting Streams
- [ ] Implement truncation: `Truncate(Stream, Revision) -> Results`.
- [ ] Implement deletion: `Delete(Stream) -> Results`.

### Streaming Operations
- [ ] Support long-lived process subscriptions.
- [ ] Implement push notification style operations.

### Other Operations
- [ ] Check if a stream exists: `StreamExist(Stream) -> Result`.
  - [x] with an existing stream
  - [x] with an unknown stream
  - [ ] with a deleted stream
- [x] Get the last revision and position: `StreamHead(Stream) -> Result`.
- [x] Get the last known position in the store: `HeadPosition() -> Position`.
- [ ] Retrieve schemas, categories, IDs, and event types: `Streams(Filter) -> string[]`.
- [ ] Count events between revisions/positions: `Count(Stream, Revision, Revision) -> Number`.



### Tests
- [ ] Test cancellation token stops enumeration. How can I do that ?

## To keep in mind

### Tell that the stream doesn't exists when the stream is read

I've temporarily removed that information from the `ReadStreamResult` to get a simpler design for now.
EventStore has a ReadState that indicates if the stream exists when read with `ReadStreamAsync`.
Later, I would like to start calling the database when iterating on events or looking at the `ReadState` as they do.

### Event read in wrong order
https://event-driven.io/en/ordering_in_postgres_outbox/
https://github.com/prooph/pdo-event-store/issues/189
https://www.youtube.com/watch?v=rm2lFlI3Ubk


## Benchmarks

Let's be honest: I know nothing about running benchmarks and I use that project to learn a bit more about it.

This project uses [BenchmarkDotNet](https://benchmarkdotnet.org) to create benchmarks.

Benchmarks are in the Benchmarks project.

To run them use
```sh
dotnet run -C Release --project Benchmarks --filter "*{BENCHMARK_NAME}*"
```

To get the list of all benchmarks use
```sh
dotnet run -c Release --project Benchmarks -- --list
```

