export class MinPriorityQueue {
  constructor() {
    this.nodes = []
    this.priorities = []
  }

  get size() {
    return this.nodes.length
  }

  push(node, priority) {
    let index = this.nodes.length
    this.nodes.push(node)
    this.priorities.push(priority)
    while (index > 0) {
      const parent = (index - 1) >> 1
      if (!this.#less(index, parent)) break
      this.#swap(index, parent)
      index = parent
    }
  }

  pop() {
    if (this.nodes.length === 0) return undefined
    const node = this.nodes[0]
    const priority = this.priorities[0]
    const lastNode = this.nodes.pop()
    const lastPriority = this.priorities.pop()
    if (this.nodes.length > 0) {
      this.nodes[0] = lastNode
      this.priorities[0] = lastPriority
      let index = 0
      for (;;) {
        const left = index * 2 + 1
        const right = left + 1
        let smallest = index
        if (left < this.nodes.length && this.#less(left, smallest)) smallest = left
        if (right < this.nodes.length && this.#less(right, smallest)) smallest = right
        if (smallest === index) break
        this.#swap(index, smallest)
        index = smallest
      }
    }
    return { node, priority }
  }

  #less(left, right) {
    const difference = this.priorities[left] - this.priorities[right]
    return difference < 0 || (difference === 0 && this.nodes[left] < this.nodes[right])
  }

  #swap(left, right) {
    ;[this.nodes[left], this.nodes[right]] = [this.nodes[right], this.nodes[left]]
    ;[this.priorities[left], this.priorities[right]] = [this.priorities[right], this.priorities[left]]
  }
}
