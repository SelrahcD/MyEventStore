

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