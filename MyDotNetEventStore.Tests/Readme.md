# MyDotNetEventStore


## Todo
## Events
- [ ] Define events as fine-grained data units in the database.
- [ ] Ensure each event has the following attributes:
    - [ ] **Type**: A string defined by the application.
    - [ ] **Stream**: The stream the event belongs to, controlled by the application.
    - [ ] **Id**: A unique string over the store (UUID).
    - [ ] **Revision**: A number representing the position in the stream.
    - [ ] **Positions**: Numbers representing the global position of the event in the stream structure.
    - [ ] **Data**: The payload of the event (JSON, byte arrays, XML, etc.).
    - [ ] **Metadata**:
        - [ ] System Metadata:
            - [ ] **Timestamp**: Date and time when the event was appended.
            - [ ] **CorrelationId**: Supplied by the application.
            - [ ] **CausationId**: Supplied by the application.
        - [ ] Application Metadata: Application-level metadata (format-agnostic).
- [ ] Use event Id for deduplication when appending data to the store.
- [ ] Use Revision for optimistic locking.
- [ ] Ensure Timestamp is not used for application-level purposes.
- [ ] Manage Revision & Positions as strictly increasing values.

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
- [ ] Implement append operation: `Append(Stream, ExpectedRevision, Event[]) -> Result`.
- [ ] Support ACID transactional behavior for appends.
- [ ] Use ExpectedRevision for optimistic locking:
    - [ ] **Any**: No concurrency check.
    - [ ] **NoStream**: Stream should not exist.
    - [ ] **StreamExists**: Stream exists, may be empty.
    - [ ] **Some number**: Match specific stream revision.
- [ ] Return new Revision and Positions after appending.

### Idempotency
- [ ] Implement idempotency checks for append operations.
- [ ] Use ExpectedRevision and EventId for idempotency behavior.

### Appending Metadata
- [ ] Allow appending metadata to streams: `AppendMetadata(Stream, ExpectedRevision, Metadata) -> Result`.
- [ ] Support appending even if the target stream does not exist.

### Reading Data and Metadata
- [ ] Implement read operations with forward or backward directions:
    - [ ] `Read(Stream, Direction, Revision) -> Results`
    - [ ] `Read(Stream, Direction, Position) -> Results`
    - [ ] `Read(Direction, Position) -> Results`
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
- [ ] Get the last revision and position: `StreamHead(Stream) -> Result`.
- [ ] Get the last known position in the store: `HeadPosition() -> Position`.
- [ ] Retrieve schemas, categories, IDs, and event types: `Streams(Filter) -> string[]`.
- [ ] Count events between revisions/positions: `Count(Stream, Revision, Revision) -> Number`.


## To add later

### EventStore should ask for a connection when needed from the pool

But how can we deal with transaction ?
-> Could use a UnitOfWork that gets a Connection and pass it to the event store ?

### Tell that the stream doesn't exists when the stream is read

I've temporarily removed that information from the `ReadStreamResult` to get a simpler design for now.
EventStore has a ReadState that indicates if the stream exists when read with `ReadStreamAsync`.
Later, I would like to start calling the database when iterating on events or looking at the `ReadState` as they do.

### Event read in wrong order
https://event-driven.io/en/ordering_in_postgres_outbox/
https://github.com/prooph/pdo-event-store/issues/189
https://www.youtube.com/watch?v=rm2lFlI3Ubk