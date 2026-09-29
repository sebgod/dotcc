"""A task scheduler in the style of Martin Richards' benchmark: classes with
inheritance, attribute access, method dispatch and linked lists of packets.
Prints how many times each kind of task ran and the queue/hold counters."""

IDLE, WORKER, HANDLER_A, HANDLER_B, DEVICE_A, DEVICE_B = range(6)
KIND_DEVICE, KIND_WORK = 0, 1
BUFSIZE = 4


class Packet:
    def __init__(self, link, ident, kind):
        self.link = link
        self.ident = ident
        self.kind = kind
        self.datum = 0
        self.data = [0] * BUFSIZE

    def append_to(self, lst):
        self.link = None
        if lst is None:
            return self
        p = lst
        while p.link is not None:
            p = p.link
        p.link = self
        return lst


class TaskState:
    def __init__(self, packet_pending=True, waiting=False, holding=False):
        self.packet_pending = packet_pending
        self.waiting = waiting
        self.holding = holding

    def is_held_or_waiting(self):
        return self.holding or (not self.packet_pending and self.waiting)

    def is_waiting_with_packet(self):
        return self.packet_pending and self.waiting and not self.holding


class Scheduler:
    def __init__(self):
        self.tasks = [None] * 6
        self.task_list = None
        self.current = None
        self.current_id = 0
        self.queue_count = 0
        self.hold_count = 0
        self.runs = [0] * 6

    def add(self, task):
        self.tasks[task.ident] = task
        task.link = self.task_list
        self.task_list = task

    def run(self):
        self.current = self.task_list
        while self.current is not None:
            if self.current.state.is_held_or_waiting():
                self.current = self.current.link
            else:
                self.current_id = self.current.ident
                self.runs[self.current.ident] += 1
                self.current = self.current.run_once()

    def find(self, ident):
        return self.tasks[ident]

    def hold_self(self):
        self.hold_count += 1
        self.current.state.holding = True
        return self.current.link

    def release(self, ident):
        t = self.find(ident)
        t.state.holding = False
        if t.priority > self.current.priority:
            return t
        return self.current

    def wait(self):
        self.current.state.waiting = True
        return self.current

    def queue(self, packet):
        t = self.find(packet.ident)
        self.queue_count += 1
        packet.link = None
        packet.ident = self.current_id
        return t.check_priority_add(self.current, packet)


class Task:
    def __init__(self, sched, ident, priority, work, state):
        self.sched = sched
        self.ident = ident
        self.priority = priority
        self.work = work
        self.state = state
        self.link = None
        sched.add(self)

    def run_once(self):
        if self.state.is_waiting_with_packet():
            packet = self.work
            self.work = packet.link
            self.state.packet_pending = self.work is not None
            self.state.waiting = False
            return self.fn(packet)
        return self.fn(None)

    def check_priority_add(self, current, packet):
        if self.work is None:
            self.work = packet
            self.state.packet_pending = True
            if self.priority > current.priority:
                return self
        else:
            self.work = packet.append_to(self.work)
        return current


class IdleTask(Task):
    def __init__(self, sched, ident, priority, count):
        super().__init__(sched, ident, priority, None, TaskState(packet_pending=False))
        self.control = 1
        self.count = count

    def fn(self, packet):
        self.count -= 1
        if self.count == 0:
            return self.sched.hold_self()
        if self.control & 1 == 0:
            self.control //= 2
            return self.sched.release(DEVICE_A)
        self.control = (self.control // 2) ^ 0xD008
        return self.sched.release(DEVICE_B)


class WorkerTask(Task):
    def __init__(self, sched, ident, priority, work):
        super().__init__(sched, ident, priority, work, TaskState(waiting=True))
        self.dest = HANDLER_A
        self.count = 0

    def fn(self, packet):
        if packet is None:
            return self.sched.wait()
        self.dest = HANDLER_B if self.dest == HANDLER_A else HANDLER_A
        packet.ident = self.dest
        packet.datum = 0
        for i in range(BUFSIZE):
            self.count += 1
            if self.count > 26:
                self.count = 1
            packet.data[i] = ord("A") + self.count - 1
        return self.sched.queue(packet)


class HandlerTask(Task):
    def __init__(self, sched, ident, priority, work):
        super().__init__(sched, ident, priority, work, TaskState(waiting=True))
        self.work_in = None
        self.device_in = None

    def fn(self, packet):
        if packet is not None:
            if packet.kind == KIND_WORK:
                self.work_in = packet.append_to(self.work_in)
            else:
                self.device_in = packet.append_to(self.device_in)
        if self.work_in is not None:
            work = self.work_in
            count = work.datum
            if count < BUFSIZE:
                if self.device_in is not None:
                    dev = self.device_in
                    self.device_in = dev.link
                    dev.datum = work.data[count]
                    work.datum = count + 1
                    return self.sched.queue(dev)
            else:
                self.work_in = work.link
                return self.sched.queue(work)
        return self.sched.wait()


class DeviceTask(Task):
    def __init__(self, sched, ident, priority):
        super().__init__(sched, ident, priority, None, TaskState(packet_pending=False))
        self.pending = None

    def fn(self, packet):
        if packet is None:
            if self.pending is None:
                return self.sched.wait()
            p = self.pending
            self.pending = None
            return self.sched.queue(p)
        self.pending = packet
        return self.sched.hold_self()


def run(count):
    sched = Scheduler()
    IdleTask(sched, IDLE, 1, count)
    wkq = Packet(None, 0, KIND_WORK)
    wkq = Packet(wkq, 0, KIND_WORK)
    WorkerTask(sched, WORKER, 1000, wkq)
    wkq = Packet(None, DEVICE_A, KIND_DEVICE)
    wkq = Packet(wkq, DEVICE_A, KIND_DEVICE)
    wkq = Packet(wkq, DEVICE_A, KIND_DEVICE)
    HandlerTask(sched, HANDLER_A, 2000, wkq)
    wkq = Packet(None, DEVICE_B, KIND_DEVICE)
    wkq = Packet(wkq, DEVICE_B, KIND_DEVICE)
    wkq = Packet(wkq, DEVICE_B, KIND_DEVICE)
    HandlerTask(sched, HANDLER_B, 3000, wkq)
    DeviceTask(sched, DEVICE_A, 4000)
    DeviceTask(sched, DEVICE_B, 5000)
    sched.run()
    return sched


for count in (100, 10000):
    s = run(count)
    print(f"count={count} queue={s.queue_count} hold={s.hold_count} runs={s.runs}")
